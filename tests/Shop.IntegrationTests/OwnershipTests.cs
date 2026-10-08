using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Features.Orders;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>Every order query is scoped by id and JWT subject; the production seed has one user, so a second one is added here.</summary>
[Collection("SQL")]
public sealed class OwnershipTests(SqlFixture fixture) : IAsyncLifetime
{
    private const string OtherEmail = "other@shop.test";
    private const string OtherPassword = "Other-Pass-123!";
    private HttpClient _owner = null!;
    private HttpClient _other = null!;
    private Guid _product;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await fixture.AddUserAsync(OtherEmail, OtherPassword);
        _owner = await fixture.AuthenticatedClientAsync();
        _other = await fixture.AuthenticatedClientAsync(OtherEmail, OtherPassword);
        await using var scope = fixture.CreateScope();
        _product = (await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Products.SingleAsync(p => p.Sku == "COF-ETH-250")).Id;
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        _other.Dispose();
        return Task.CompletedTask;
    }

    private object Items(int quantity) => new[] { new { productId = _product, quantity } };

    private static async Task<OrderResponse> Create(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/orders", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
    }

    private static async Task<(string Title, string Detail)> NotFoundProblem(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("title").GetString()!, json.RootElement.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Another_user_gets_404_on_get_put_and_delete_of_an_existing_order_and_nothing_changes()
    {
        var order = await Create(_owner, new { items = Items(2), couponCode = "SAVE5" });

        using var get = await _other.GetAsync($"/orders/{order.Id}");
        using var put = await _other.PutAsJsonAsync($"/orders/{order.Id}", new { items = Items(9), couponCode = (string?)null });
        using var delete = await _other.DeleteAsync($"/orders/{order.Id}");

        await NotFoundProblem(get);
        await NotFoundProblem(put);
        await NotFoundProblem(delete);
        using var still = await _owner.GetAsync($"/orders/{order.Id}");
        Assert.Equivalent(order, await still.Content.ReadFromJsonAsync<OrderResponse>(), strict: true);
    }

    [Fact]
    public async Task A_foreign_order_is_indistinguishable_from_a_missing_one()
    {
        var order = await Create(_owner, new { items = Items(1) });
        using var foreign = await _other.GetAsync($"/orders/{order.Id}");
        using var missing = await _other.GetAsync($"/orders/{Guid.NewGuid()}");
        Assert.Equal(await NotFoundProblem(missing), await NotFoundProblem(foreign));
    }

    [Fact]
    public async Task Each_user_sees_and_changes_only_their_own_orders()
    {
        var mine = await Create(_owner, new { items = Items(1) });
        var theirs = await Create(_other, new { items = Items(4) });

        using var ownerSeesOwn = await _owner.GetAsync($"/orders/{mine.Id}");
        using var otherSeesOwn = await _other.GetAsync($"/orders/{theirs.Id}");
        using var ownerSeesForeign = await _owner.GetAsync($"/orders/{theirs.Id}");
        Assert.Equal(HttpStatusCode.OK, ownerSeesOwn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherSeesOwn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ownerSeesForeign.StatusCode);

        using var delete = await _other.DeleteAsync($"/orders/{theirs.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var ownerStillHasOwn = await _owner.GetAsync($"/orders/{mine.Id}");
        Assert.Equal(HttpStatusCode.OK, ownerStillHasOwn.StatusCode);
    }

    [Fact]
    public async Task A_validly_signed_token_for_a_subject_without_orders_sees_nothing()
    {
        var order = await Create(_owner, new { items = Items(1) });
        using var stranger = fixture.Factory.CreateClient();
        stranger.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create());
        using var response = await stranger.GetAsync($"/orders/{order.Id}");
        await NotFoundProblem(response);
    }
}
