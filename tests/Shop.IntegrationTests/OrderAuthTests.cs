using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Features.Orders;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>Bearer rejection on the real order routes: each is 401 with a Problem Details body and never touches data.</summary>
[Collection("SQL")]
public sealed class OrderAuthTests(SqlFixture fixture) : IAsyncLifetime
{
    private Guid _orderId;
    private Guid _product;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var scope = fixture.CreateScope();
        _product = (await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Products.SingleAsync(p => p.Sku == "COF-ETH-250")).Id;
        using var client = await fixture.AuthenticatedClientAsync();
        using var response = await client.PostAsJsonAsync("/orders", new { items = new[] { new { productId = _product, quantity = 2 } } });
        _orderId = (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string, string> Routes => new()
    {
        { "POST", "/orders" },
        { "GET", "/orders/@id" },
        { "PUT", "/orders/@id" },
        { "DELETE", "/orders/@id" }
    };

    public static TheoryData<string> BadTokens => new() { "missing", "foreign-signature", "expired", "garbage", "bad-subject" };

    private static string? TokenFor(string kind) => kind switch
    {
        "missing" => null,
        "foreign-signature" => TestTokens.Create(key: TestTokens.ForeignKey),
        "expired" => TestTokens.Expired(),
        "garbage" => "not.a.jwt",
        "bad-subject" => TestTokens.Create(d => d.Claims["sub"] = "not-a-guid"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task<HttpResponseMessage> Send(string method, string path, string? token)
    {
        using var client = fixture.Factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var request = new HttpRequestMessage(new HttpMethod(method), path.Replace("@id", _orderId.ToString()));
        if (method is "POST" or "PUT")
            request.Content = JsonContent.Create(new { items = new[] { new { productId = _product, quantity = 1 } }, couponCode = (string?)null });
        return await client.SendAsync(request);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Missing_foreign_signature_and_expired_tokens_get_a_401_problem_on_every_order_route(string method, string path)
    {
        foreach (var kind in new[] { "missing", "foreign-signature", "expired" })
        {
            using var response = await Send(method, path, TokenFor(kind));
            Assert.True(HttpStatusCode.Unauthorized == response.StatusCode, $"{method} {path} with {kind} token returned {(int)response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        }
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Every_unusable_token_is_rejected_before_the_handler_runs(string kind)
    {
        using var response = await Send("DELETE", "/orders/@id", TokenFor(kind));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.Equal((1, 1), (await db.Orders.CountAsync(), await db.OrderItems.CountAsync()));
    }

    [Fact]
    public async Task Rejected_create_and_update_calls_leave_the_data_untouched()
    {
        foreach (var method in new[] { "POST", "PUT" })
        {
            using var response = await Send(method, method == "POST" ? "/orders" : "/orders/@id", TestTokens.Create(key: TestTokens.ForeignKey));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await using var scope = fixture.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Orders.Include(o => o.Items).SingleAsync();
        Assert.Equal(2, order.Items.Single().Quantity);
    }
}
