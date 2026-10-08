using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Domain;
using Shop.Api.Features.Orders;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>
/// Deterministic SQL experiments for stale writers whose replacement only ADDS a line. EF may run the
/// OrderItems INSERT before the guarded Orders UPDATE, so these shapes could fail a constraint instead of the rowversion check.
/// </summary>
[Collection("SQL")]
public sealed class OrderInsertConflictTests(SqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private Guid _userId;
    private Dictionary<string, Product> _products = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        _userId = (await db.Users.SingleAsync()).Id;
        _products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OrderLine Line(string sku, int quantity)
    {
        var p = _products[sku];
        return new OrderLine(p.Id, p.Sku, p.Name, p.Price, quantity, p.StockQuantity);
    }

    private async Task<Guid> SeedOrder()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var order = Order.Create(_userId, [Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1)], null, T0).Value;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static Task<Order> Load(ShopDbContext db, Guid id) => db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == id);

    private static string Describe(Exception? exception)
    {
        var parts = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException)
            parts.Add(e is SqlException sql ? $"{e.GetType().Name}(Number={sql.Number}: {sql.Message})" : e.GetType().Name);
        return string.Join(" -> ", parts);
    }

    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlForeignKeyViolation = 547;

    private static int? ConstraintNumber(Exception? exception)
    {
        Assert.IsType<DbUpdateException>(exception);
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is SqlException sql) return sql.Number;
        return null;
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception exception) { return exception; }
    }

    [Fact]
    public async Task Raw_save_of_two_stale_writers_adding_the_same_product_fails_atomically_leaving_only_the_winner()
    {
        var id = await SeedOrder();
        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ShopDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ShopDbContext>();
        var a = await Load(dbA, id);
        var b = await Load(dbB, id);

        Assert.Null(b.Replace([Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1), Line("ACC-MUG-12", 4)], null, T0));
        await dbB.SaveLiveOrderAsync(b);
        Assert.Null(a.Replace([Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1), Line("ACC-MUG-12", 1)], null, T0));

        var failure = await Capture(() => dbA.SaveLiveOrderAsync(a));

        // The raw save loses at the unique index (2601) before the guarded header UPDATE; the whole save rolled back.
        Assert.Equal(SqlUniqueIndexViolation, ConstraintNumber(failure));
        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal(b.Version, fresh.Version);
        Assert.Equal([("ACC-MUG-12", 4), ("COF-ETH-250", 2), ("TEA-EAR-100", 1)], fresh.Items.OrderBy(i => i.Sku).Select(i => (i.Sku, i.Quantity)));
    }

    [Fact]
    public async Task Raw_save_of_an_add_only_stale_writer_after_a_delete_fails_atomically_leaving_nothing()
    {
        var id = await SeedOrder();
        await using var scopeA = fixture.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ShopDbContext>();
        var a = await Load(dbA, id);
        await using (var scopeB = fixture.CreateScope())
        {
            var dbB = scopeB.ServiceProvider.GetRequiredService<ShopDbContext>();
            dbB.Orders.Remove(await dbB.Orders.SingleAsync(o => o.Id == id));
            await dbB.SaveChangesAsync();
        }

        Assert.Null(a.Replace([Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1), Line("ACC-MUG-12", 1)], null, T0));
        var failure = await Capture(() => dbA.SaveLiveOrderAsync(a));

        // The raw save loses at the OrderItems foreign key (547): the order is gone.
        Assert.Equal(SqlForeignKeyViolation, ConstraintNumber(failure));
        await using var scopeC = fixture.CreateScope();
        var check = scopeC.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.Equal((0, 0), (await check.Orders.CountAsync(), await check.OrderItems.CountAsync()));
    }

    // ---- the same shapes through the real HTTP service ----

    private async Task<(HttpClient Client, Guid OrderId)> CreateViaHttp()
    {
        var client = await fixture.AuthenticatedClientAsync();
        using var response = await client.PostAsJsonAsync("/orders", new
        {
            items = new[] { new { productId = _products["COF-ETH-250"].Id, quantity = 2 }, new { productId = _products["TEA-EAR-100"].Id, quantity = 1 } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id);
    }

    private object AddMug(int quantity) => new
    {
        items = new[]
        {
            new { productId = _products["COF-ETH-250"].Id, quantity = 2 },
            new { productId = _products["TEA-EAR-100"].Id, quantity = 1 },
            new { productId = _products["ACC-MUG-12"].Id, quantity }
        },
        couponCode = (string?)null
    };

    private async Task<JsonElement> ExpectConflictProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Contains("Reload", problem.GetProperty("detail").GetString());
        return problem;
    }

    [Fact]
    public async Task Http_put_losing_a_same_new_product_race_is_409_and_the_winners_state_survives()
    {
        var (client, id) = await CreateViaHttp();
        using var _ = client;
        try
        {
            // Runs inside the PUT's save, after the request loaded the order: the winner adds the same product first.
            fixture.SaveFailures.BeforeNextSave(async () =>
            {
                await using var scope = fixture.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
                var winner = await Load(db, id);
                Assert.Null(winner.Replace([Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1), Line("ACC-MUG-12", 4)], null, T0.AddMinutes(1)));
                await db.SaveLiveOrderAsync(winner);
            });
            using var put = await client.PutAsJsonAsync($"/orders/{id}", AddMug(1));
            await ExpectConflictProblem(put);
        }
        finally { fixture.SaveFailures.Disarm(); }

        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal([("ACC-MUG-12", 4), ("COF-ETH-250", 2), ("TEA-EAR-100", 1)], fresh.Items.OrderBy(i => i.Sku).Select(i => (i.Sku, i.Quantity)));
        Assert.Equal(T0.AddMinutes(1), fresh.UpdatedAt);
    }

    [Fact]
    public async Task Http_add_only_put_after_a_concurrent_delete_is_409_and_nothing_is_left()
    {
        var (client, id) = await CreateViaHttp();
        using var _ = client;
        try
        {
            fixture.SaveFailures.BeforeNextSave(async () =>
            {
                await using var scope = fixture.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
                db.Orders.Remove(await db.Orders.SingleAsync(o => o.Id == id));
                await db.SaveChangesAsync();
            });
            using var put = await client.PutAsJsonAsync($"/orders/{id}", AddMug(1));
            await ExpectConflictProblem(put);
        }
        finally { fixture.SaveFailures.Disarm(); }

        await using var scopeC = fixture.CreateScope();
        var check = scopeC.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.Equal((0, 0), (await check.Orders.CountAsync(), await check.OrderItems.CountAsync()));
        using var get = await client.GetAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Theory]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.OrderItems' with unique index 'IX_OrderItems_OrderId_ProductId'.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_OrderItems_Products_ProductId\".")]
    [InlineData(547, "The INSERT statement conflicted with the CHECK constraint \"CK_OrderItems_Quantity\".")]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.Products' with unique index 'IX_Products_Sku'.")]
    public async Task A_constraint_failure_on_an_order_that_is_not_stale_is_not_reported_as_409(int number, string message)
    {
        var (client, id) = await CreateViaHttp();
        using var _ = client;
        try
        {
            fixture.SaveFailures.FailNextSave(new DbUpdateException("Save failed.", Shop.TestSupport.SqlServerErrors.Create(number, message)));
            using var put = await client.PutAsJsonAsync($"/orders/{id}", AddMug(1));
            Assert.Equal(HttpStatusCode.InternalServerError, put.StatusCode);
        }
        finally { fixture.SaveFailures.Disarm(); }

        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal([("COF-ETH-250", 2), ("TEA-EAR-100", 1)], fresh.Items.OrderBy(i => i.Sku).Select(i => (i.Sku, i.Quantity)));
    }
}
