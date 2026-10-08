using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Domain;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>Aggregate persistence and the deterministic stale-write proofs (no HTTP, no timing).</summary>
[Collection("SQL")]
public sealed class OrderPersistenceTests(SqlFixture fixture) : IAsyncLifetime
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

    private static readonly CouponSnapshot Save5 = new("SAVE5", 5.00m);

    private async Task<Guid> SeedOrder(CouponSnapshot? coupon = null)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var order = Order.Create(_userId, [Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1)], coupon, T0).Value;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static Task<Order> Load(ShopDbContext db, Guid id) => db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == id);

    private static (string Sku, int Quantity, decimal Price)[] Items(Order order)
        => order.Items.OrderBy(i => i.Sku).Select(i => (i.Sku, i.Quantity, i.UnitPrice)).ToArray();

    [Fact]
    public async Task A_created_aggregate_round_trips_with_snapshots_a_version_and_one_save()
    {
        var id = await SeedOrder(Save5);

        await using var scope = fixture.CreateScope();
        var order = await Load(scope.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal((_userId, OrderStatus.Draft, T0, T0), (order.UserId, order.Status, order.CreatedAt, order.UpdatedAt));
        Assert.Equal(("SAVE5", 5.00m), (order.CouponCode, order.CouponAmount));
        Assert.Equal([("COF-ETH-250", 2, 14.50m), ("TEA-EAR-100", 1, 8.60m)], Items(order));
        Assert.Equal(8, order.Version.Length);
        Assert.All(order.Items, i => Assert.Equal(id, i.OrderId));
        Assert.Equal(new OrderTotals(37.60m, 5.00m, 32.60m), order.Totals());
    }

    [Fact]
    public async Task A_replacement_persists_exactly_the_diff_in_one_save()
    {
        var id = await SeedOrder(Save5);
        byte[] oldVersion;
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            var order = await Load(db, id);
            oldVersion = order.Version;
            Assert.Null(order.Replace([Line("COF-ETH-250", 5), Line("ACC-MUG-12", 1)], null, T0.AddMinutes(1)));
            Assert.True(await db.SaveLiveOrderAsync(order) > 0);
        }

        await using var verify = fixture.CreateScope();
        var fresh = await Load(verify.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal([("ACC-MUG-12", 1, 9.90m), ("COF-ETH-250", 5, 14.50m)], Items(fresh));
        Assert.Equal((null, null, T0.AddMinutes(1)), (fresh.CouponCode, fresh.CouponAmount, fresh.UpdatedAt));
        Assert.NotEqual(oldVersion, fresh.Version);
        await using var raw = fixture.CreateScope();
        Assert.Equal(2, await raw.ServiceProvider.GetRequiredService<ShopDbContext>().OrderItems.CountAsync(i => i.OrderId == id));
    }

    [Fact]
    public async Task A_stale_save_conflicts_and_the_fresh_state_is_exactly_the_winning_writers()
    {
        var id = await SeedOrder();
        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ShopDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ShopDbContext>();
        var a = await Load(dbA, id);
        var b = await Load(dbB, id);
        Assert.Equal(a.Version, b.Version);

        // Both writers use the same timestamp as the stored one, so only the forced header update can carry the guard.
        Assert.Null(b.Replace([Line("COF-ETH-250", 7), Line("TEA-CHA-100", 2)], Save5, T0));
        await dbB.SaveLiveOrderAsync(b);

        Assert.Null(a.Replace([Line("COF-ETH-250", 1), Line("ACC-MUG-12", 4), Line("TEA-SEN-100", 1)], new CouponSnapshot("SAVE10", 10.00m), T0));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbA.SaveLiveOrderAsync(a));

        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal((OrderStatus.Draft, "SAVE5", 5.00m, T0, T0), (fresh.Status, fresh.CouponCode, fresh.CouponAmount, fresh.CreatedAt, fresh.UpdatedAt));
        Assert.Equal(b.Version, fresh.Version);
        Assert.Equal([("COF-ETH-250", 7, 14.50m), ("TEA-CHA-100", 2, 9.30m)], Items(fresh));
        await using var raw = fixture.CreateScope();
        Assert.Equal(2, await raw.ServiceProvider.GetRequiredService<ShopDbContext>().OrderItems.CountAsync());
    }

    [Fact]
    public async Task Without_the_forced_header_update_a_child_only_change_would_not_be_guarded()
    {
        // Documents why SaveLiveOrderAsync exists: a plain save of a child-only edit at an unchanged timestamp
        // issues no Orders UPDATE, so the stale writer silently overwrites.
        var id = await SeedOrder();
        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ShopDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ShopDbContext>();
        var a = await Load(dbA, id);
        var b = await Load(dbB, id);
        Assert.Null(b.Replace([Line("COF-ETH-250", 7), Line("TEA-EAR-100", 1)], null, T0));
        await dbB.SaveLiveOrderAsync(b);

        Assert.Null(a.Replace([Line("COF-ETH-250", 9), Line("TEA-EAR-100", 1)], null, T0));
        await dbA.SaveChangesAsync(); // no conflict: the lost update the forced modification prevents

        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal(9, fresh.Items.Single(i => i.Sku == "COF-ETH-250").Quantity);
    }

    [Fact]
    public async Task Deleting_with_a_stale_version_conflicts_and_keeps_the_order()
    {
        var id = await SeedOrder();
        await using var scopeA = fixture.CreateScope();
        await using var scopeB = fixture.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ShopDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ShopDbContext>();
        var a = await dbA.Orders.SingleAsync(o => o.Id == id);
        var b = await Load(dbB, id);
        Assert.Null(b.Replace([Line("ACC-MUG-12", 3)], null, T0.AddMinutes(1)));
        await dbB.SaveLiveOrderAsync(b);

        dbA.Orders.Remove(a);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbA.SaveChangesAsync());

        await using var scopeC = fixture.CreateScope();
        var fresh = await Load(scopeC.ServiceProvider.GetRequiredService<ShopDbContext>(), id);
        Assert.Equal([("ACC-MUG-12", 3, 9.90m)], Items(fresh));
    }

    [Fact]
    public async Task Deleting_cascades_the_items_in_a_single_save()
    {
        var id = await SeedOrder();
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            db.Orders.Remove(await db.Orders.SingleAsync(o => o.Id == id));
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateScope();
        var check = verify.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.Equal((0, 0), (await check.Orders.CountAsync(), await check.OrderItems.CountAsync()));
    }

    [Fact]
    public async Task Saving_against_an_order_that_was_deleted_meanwhile_conflicts()
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

        Assert.Null(a.Replace([Line("ACC-MUG-12", 1)], null, T0.AddMinutes(1)));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbA.SaveLiveOrderAsync(a));
    }
}
