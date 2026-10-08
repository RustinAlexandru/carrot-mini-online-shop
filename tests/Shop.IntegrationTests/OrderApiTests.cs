using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Features.Orders;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>Owned-order API through the real middleware, handlers, EF and SQL Server.</summary>
[Collection("SQL")]
public sealed class OrderApiTests(SqlFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private Dictionary<string, Guid> _ids = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _client = await fixture.AuthenticatedClientAsync();
        await using var scope = fixture.CreateScope();
        _ids = await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Products.AsNoTracking().ToDictionaryAsync(p => p.Sku, p => p.Id);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private object Item(string sku, int quantity) => new { productId = _ids[sku], quantity };

    private Task<HttpResponseMessage> Post(object body) => _client.PostAsJsonAsync("/orders", body);
    private Task<HttpResponseMessage> Put(Guid id, object body) => _client.PutAsJsonAsync($"/orders/{id}", body);

    private async Task<OrderResponse> Created(object body)
    {
        using var response = await Post(body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private async Task<OrderResponse> Get(Guid id)
    {
        using var response = await _client.GetAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static (string Sku, int Quantity, decimal Price, decimal Line)[] Lines(OrderResponse order)
        => order.Items.Select(i => (i.Sku, i.Quantity, i.UnitPrice, i.LineTotal)).OrderBy(i => i.Sku).ToArray();

    private async Task Sql(Func<ShopDbContext, Task> action)
    {
        await using var scope = fixture.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ShopDbContext>());
    }

    // ---- create / read ----

    [Fact]
    public async Task Create_returns_201_with_location_server_prices_and_exact_totals()
    {
        using var response = await Post(new { items = new[] { Item("COF-ETH-250", 2), Item("COF-COL-250", 1) }, couponCode = "SAVE5" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal($"/orders/{order.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Draft", order.Status);
        Assert.Equal([("COF-COL-250", 1, 12.90m, 12.90m), ("COF-ETH-250", 2, 14.50m, 29.00m)], Lines(order));
        Assert.Equal((41.90m, 5.00m, 36.90m, "USD"), (order.Subtotal, order.Discount, order.Total, order.Currency));
        Assert.Equal(new CouponResponse("SAVE5", 5.00m), order.Coupon);
        Assert.Equal(order.CreatedAt, order.UpdatedAt);
    }

    [Fact]
    public async Task Response_uses_the_documented_property_names()
    {
        using var response = await Post(new { items = new[] { Item("COF-ETH-250", 1) }, couponCode = "SAVE5" });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        foreach (var name in new[] { "id", "status", "createdAt", "updatedAt", "items", "coupon", "subtotal", "discount", "total", "currency" })
            Assert.True(root.TryGetProperty(name, out _), $"missing {name}");
        foreach (var name in new[] { "productId", "sku", "name", "unitPrice", "quantity", "lineTotal" })
            Assert.True(root.GetProperty("items")[0].TryGetProperty(name, out _), $"missing items.{name}");
        Assert.False(root.TryGetProperty("version", out _));
        Assert.False(root.TryGetProperty("userId", out _));
    }

    [Fact]
    public async Task Create_without_couponCode_means_no_coupon()
    {
        var order = await Created(new { items = new[] { Item("COF-ETH-250", 1) } });
        Assert.Null(order.Coupon);
        Assert.Equal((14.50m, 0m, 14.50m), (order.Subtotal, order.Discount, order.Total));
    }

    [Fact]
    public async Task Get_returns_the_stored_order_with_the_same_values_as_create()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2), Item("TEA-EAR-100", 3) }, couponCode = "SAVE10" });
        Assert.Equivalent(created, await Get(created.Id), strict: true);
    }

    [Fact]
    public async Task A_coupon_larger_than_the_subtotal_is_capped_and_the_total_is_zero()
    {
        var order = await Created(new { items = new[] { Item("ACC-MUG-12", 1) }, couponCode = "SAVE10" });
        Assert.Equal((9.90m, 9.90m, 0m), (order.Subtotal, order.Discount, order.Total));
        Assert.Equal(10.00m, order.Coupon!.Amount); // the snapshot keeps the coupon's own amount
    }

    [Theory]
    [InlineData("save5", "SAVE5", 5.00)]
    [InlineData("  Save10 ", "SAVE10", 10.00)]
    public async Task Coupon_lookup_ignores_case_and_surrounding_spaces(string typed, string code, double amount)
    {
        var order = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = typed });
        Assert.Equal(new CouponResponse(code, (decimal)amount), order.Coupon);
    }

    // ---- full lifecycle ----

    [Fact]
    public async Task Full_lifecycle_create_get_replace_get_delete_then_404()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2), Item("COF-COL-250", 1) }, couponCode = "SAVE5" });

        // add TEA-SEN, change ETH quantity, remove COL, swap the coupon
        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 4), Item("TEA-SEN-100", 1) }, couponCode = "SAVE10" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal([("COF-ETH-250", 4, 14.50m, 58.00m), ("TEA-SEN-100", 1, 11.20m, 11.20m)], Lines(updated));
        Assert.Equal((69.20m, 10.00m, 59.20m), (updated.Subtotal, updated.Discount, updated.Total));
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.Equivalent(updated, await Get(created.Id), strict: true);

        using var delete = await _client.DeleteAsync($"/orders/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var after = await _client.GetAsync($"/orders/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        using var again = await _client.DeleteAsync($"/orders/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        await Sql(async db => Assert.Equal((0, 0), (await db.Orders.CountAsync(), await db.OrderItems.CountAsync())));
    }

    [Fact]
    public async Task Put_with_an_explicit_null_coupon_removes_it()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = (string?)null });
        var updated = (await put.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Null(updated.Coupon);
        Assert.Equal((29.00m, 0m, 29.00m), (updated.Subtotal, updated.Discount, updated.Total));
        Assert.Null((await Get(created.Id)).Coupon);
    }

    [Fact]
    public async Task Put_without_the_couponCode_member_is_rejected_and_changes_nothing()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 1) } });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equivalent(created, await Get(created.Id), strict: true);
    }

    [Fact]
    public async Task Put_without_the_items_member_is_rejected()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) } });
        using var put = await Put(created.Id, new { couponCode = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ---- snapshots and server-side prices ----

    [Fact]
    public async Task Get_keeps_saved_prices_and_coupon_while_put_reprices_from_current_catalog()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        await Sql(async db =>
        {
            await db.Products.Where(p => p.Sku == "COF-ETH-250").ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 20.00m));
            await db.Coupons.Where(c => c.Code == "SAVE5").ExecuteUpdateAsync(s => s.SetProperty(c => c.Amount, 7.00m));
        });

        var stable = await Get(created.Id);
        Assert.Equal((14.50m, 5.00m, 24.00m), (stable.Items.Single().UnitPrice, stable.Coupon!.Amount, stable.Total));

        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        var repriced = (await put.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal((20.00m, 7.00m, 33.00m), (repriced.Items.Single().UnitPrice, repriced.Coupon!.Amount, repriced.Total));
        Assert.Equivalent(repriced, await Get(created.Id), strict: true);
    }

    [Fact]
    public async Task Put_reprices_lines_even_when_the_coupon_is_removed_and_nothing_else_changes()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 1) } });
        await Sql(db => db.Products.Where(p => p.Sku == "COF-ETH-250").ExecuteUpdateAsync(s => s.SetProperty(p => p.Price, 16.00m)));
        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 1) }, couponCode = (string?)null });
        Assert.Equal(16.00m, (await put.Content.ReadFromJsonAsync<OrderResponse>())!.Total);
    }

    [Theory]
    [InlineData("""{"items":[{"productId":"@P","quantity":1,"unitPrice":0.01}]}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1,"price":0.01}]}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1}],"total":0.01}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1}],"discount":99}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1}],"userId":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1}],"status":"Expired"}""")]
    [InlineData("""{"items":[{"productId":"@P","quantity":1}],"version":"AAAAAAAAB9E="}""")]
    public async Task Client_supplied_prices_totals_ids_and_status_are_rejected(string template)
    {
        using var content = new StringContent(template.Replace("@P", _ids["COF-ETH-250"].ToString()), Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync("/orders", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await Sql(async db => Assert.Equal(0, await db.Orders.CountAsync()));
    }

    // ---- invalid input ----

    public static TheoryData<string> InvalidBodies => new()
    {
        """{"items":[],"couponCode":null}""",
        """{"items":null,"couponCode":null}""",
        """{"items":[{"productId":"@ETH","quantity":0}],"couponCode":null}""",
        """{"items":[{"productId":"@ETH","quantity":-3}],"couponCode":null}""",
        """{"items":[{"productId":"@ETH","quantity":10001}],"couponCode":null}""",
        """{"items":[{"productId":"@ETH","quantity":99}],"couponCode":null}""", // over stock (40)
        """{"items":[{"productId":"@BRA","quantity":4}],"couponCode":null}""", // stock 3
        """{"items":[{"productId":"@DEC","quantity":1}],"couponCode":null}""", // stock 0
        """{"items":[{"productId":"@ETH","quantity":1},{"productId":"@ETH","quantity":2}],"couponCode":null}""",
        """{"items":[{"productId":"00000000-0000-0000-0000-000000000000","quantity":1}],"couponCode":null}""",
        """{"items":[{"productId":"22222222-2222-2222-2222-222222222222","quantity":1}],"couponCode":null}""",
        """{"items":[{"quantity":1}],"couponCode":null}""",
        """{"items":[{"productId":"@ETH"}],"couponCode":null}""",
        """{"items":[null],"couponCode":null}""",
        """{"items":[{"productId":"@ETH","quantity":1}],"couponCode":"NOPE"}""",
        """{"items":[{"productId":"@ETH","quantity":1}],"couponCode":""}""",
        """{"items":[{"productId":"@ETH","quantity":1}],"couponCode":"   "}"""
    };

    private string Fill(string template) => template
        .Replace("@ETH", _ids["COF-ETH-250"].ToString()).Replace("@BRA", _ids["COF-BRA-1KG"].ToString()).Replace("@DEC", _ids["COF-DEC-250"].ToString());

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_input_is_a_400_on_create_and_on_update_and_an_update_changes_nothing(string template)
    {
        var body = Fill(template);
        using var create = await _client.PostAsync("/orders", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        await Sql(async db => Assert.Equal(0, await db.Orders.CountAsync()));

        var existing = await Created(new { items = new[] { Item("COF-ETH-250", 2), Item("TEA-EAR-100", 1) }, couponCode = "SAVE5" });
        using var update = await _client.PutAsync($"/orders/{existing.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equivalent(existing, await Get(existing.Id), strict: true);
    }

    [Fact]
    public async Task Validation_problem_names_the_offending_fields()
    {
        // Unresolvable input (unknown coupon/product, missing members) is reported first; once everything resolves the aggregate reports its own field errors.
        using var unresolved = await Post(new { items = new[] { Item("COF-ETH-250", 1) }, couponCode = "NOPE" });
        Assert.Equal(HttpStatusCode.BadRequest, unresolved.StatusCode);
        Assert.Equal("Unknown coupon code.", (await Problem(unresolved)).GetProperty("errors").GetProperty("couponCode")[0].GetString());

        using var domain = await Post(new { items = new[] { Item("COF-BRA-1KG", 4), Item("COF-ETH-250", 0) }, couponCode = "SAVE5" });
        Assert.Equal(HttpStatusCode.BadRequest, domain.StatusCode);
        var errors = (await Problem(domain)).GetProperty("errors");
        Assert.Equal("Only 3 in stock.", errors.GetProperty("items[0].quantity")[0].GetString());
        Assert.Equal("Quantity must be between 1 and 10000.", errors.GetProperty("items[1].quantity")[0].GetString());
    }

    [Fact]
    public async Task Stock_ceiling_allows_exactly_the_available_quantity_and_reserves_nothing()
    {
        var first = await Created(new { items = new[] { Item("COF-BRA-1KG", 3) } });
        var second = await Created(new { items = new[] { Item("COF-BRA-1KG", 3) } });
        Assert.NotEqual(first.Id, second.Id);
        await Sql(async db => Assert.Equal(3, (await db.Products.SingleAsync(p => p.Sku == "COF-BRA-1KG")).StockQuantity));
    }

    [Theory]
    [InlineData("""{"items":[{"productId":"@""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Malformed_or_empty_bodies_are_400(string body)
    {
        using var create = await _client.PostAsync("/orders", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var existing = await Created(new { items = new[] { Item("COF-ETH-250", 1) } });
        using var update = await _client.PutAsync($"/orders/{existing.Id}", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    // ---- 404 / 409 ----

    [Fact]
    public async Task Unknown_ids_are_404_for_every_verb_and_a_non_guid_route_never_matches()
    {
        var missing = Guid.NewGuid();
        using var get = await _client.GetAsync($"/orders/{missing}");
        using var put = await Put(missing, new { items = new[] { Item("COF-ETH-250", 1) }, couponCode = (string?)null });
        using var delete = await _client.DeleteAsync($"/orders/{missing}");
        using var notAGuid = await _client.GetAsync("/orders/not-a-guid");
        foreach (var response in new[] { get, put, delete, notAGuid })
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Order not found.", (await Problem(get)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_expired_order_is_viewable_and_deletable_but_not_editable()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        await Sql(db => db.Orders.ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, Shop.Api.Domain.OrderStatus.Expired)));

        Assert.Equal("Expired", (await Get(created.Id)).Status);
        using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 5) }, couponCode = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(409, (await Problem(put)).GetProperty("status").GetInt32());
        var unchanged = await Get(created.Id);
        Assert.Equal((2, "SAVE5"), (unchanged.Items.Single().Quantity, unchanged.Coupon!.Code));
        using var delete = await _client.DeleteAsync($"/orders/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task A_stale_save_reaches_the_client_as_409_through_the_real_exception_handler_and_changes_nothing()
    {
        // Not a concurrent-request race: the injected failure stands in for what EF throws on a rowversion mismatch.
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) }, couponCode = "SAVE5" });
        try
        {
            fixture.SaveFailures.FailNextSave(new DbUpdateConcurrencyException("Injected stale rowversion."));
            using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 9) }, couponCode = (string?)null });
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
            var problem = await Problem(put);
            Assert.Equal(409, problem.GetProperty("status").GetInt32());
            Assert.Contains("Reload", problem.GetProperty("detail").GetString());
        }
        finally { fixture.SaveFailures.Disarm(); }

        Assert.Equivalent(created, await Get(created.Id), strict: true);
    }

    [Fact]
    public async Task A_deadlock_victim_wrapped_by_ef_is_also_409_for_update_and_delete()
    {
        var created = await Created(new { items = new[] { Item("COF-ETH-250", 2) } });
        try
        {
            fixture.SaveFailures.FailNextSave(new DbUpdateException("Save failed.", Shop.TestSupport.SqlServerErrors.Create(1205)));
            using var put = await Put(created.Id, new { items = new[] { Item("COF-ETH-250", 3) }, couponCode = (string?)null });
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

            fixture.SaveFailures.FailNextSave(new DbUpdateException("Save failed.", Shop.TestSupport.SqlServerErrors.Create(1205)));
            using var delete = await _client.DeleteAsync($"/orders/{created.Id}");
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        }
        finally { fixture.SaveFailures.Disarm(); }

        Assert.Equivalent(created, await Get(created.Id), strict: true);
    }

    [Fact]
    public async Task Unrelated_persistence_failures_are_not_reported_as_conflicts()
    {
        try
        {
            fixture.SaveFailures.FailNextSave(new InvalidOperationException("boom"));
            using var response = await Post(new { items = new[] { Item("COF-ETH-250", 1) } });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally { fixture.SaveFailures.Disarm(); }
        await Sql(async db => Assert.Equal(0, await db.Orders.CountAsync()));
    }
}
