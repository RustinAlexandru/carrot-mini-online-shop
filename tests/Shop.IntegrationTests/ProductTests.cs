using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Api.Data;
using Shop.Api.Domain;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class ProductTests(SqlFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _client = await fixture.AuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private static readonly string[] NamesAscending =
    [
        "Brazil Santos 1kg", "Burr Hand Grinder", "Ceramic Pour-Over Dripper", "Ceremonial Matcha 30g",
        "Colombia Huila 250g", "Earl Grey 100g", "Ethiopia Yirgacheffe 250g", "House Espresso Blend 500g",
        "Japanese Sencha 100g", "Masala Chai 100g", "Stoneware Mug 12oz", "Swiss Water Decaf 250g"
    ];

    private async Task<JsonElement> GetPage(string query, HttpClient? client = null)
    {
        using var response = await (client ?? _client).GetAsync("/products" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] Names(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray();
    private static decimal[] Prices(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("price").GetDecimal()).ToArray();
    private static DateTimeOffset[] Created(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("createdAt").GetDateTimeOffset()).ToArray();

    [Fact]
    public async Task Default_request_returns_the_first_twelve_by_name_with_paging_metadata()
    {
        var page = await GetPage("");
        Assert.Equal(NamesAscending, Names(page));
        Assert.Equal((1, 12, 12, 1), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(),
            page.GetProperty("totalCount").GetInt32(), page.GetProperty("totalPages").GetInt32()));
        var first = page.GetProperty("items")[0];
        foreach (var field in new[] { "id", "sku", "name", "description", "category", "price", "stockQuantity", "createdAt", "updatedAt" })
            Assert.True(first.TryGetProperty(field, out _), $"missing {field}");
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Name_sort_orders_both_directions(string direction)
    {
        var expected = direction == "asc" ? NamesAscending : NamesAscending.Reverse().ToArray();
        Assert.Equal(expected, Names(await GetPage($"?sortBy=name&sortDirection={direction}")));
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Price_sort_orders_both_directions(string direction)
    {
        decimal[] ascending = [8.60m, 9.30m, 9.90m, 11.20m, 12.90m, 13.40m, 14.50m, 18.75m, 21.90m, 24.00m, 31.00m, 59.00m];
        var expected = direction == "asc" ? ascending : ascending.Reverse().ToArray();
        Assert.Equal(expected, Prices(await GetPage($"?sortBy=price&sortDirection={direction}")));
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task CreatedAt_sort_orders_both_directions(string direction)
    {
        var created = Created(await GetPage($"?sortBy=createdAt&sortDirection={direction}"));
        Assert.Equal(12, created.Length);
        Assert.Equal(direction == "asc" ? created.Order() : created.OrderDescending(), created);
        Assert.Equal(12, created.Distinct().Count());
    }

    [Fact]
    public async Task Pages_hold_the_requested_slice_and_report_totals()
    {
        var third = await GetPage("?page=3&pageSize=5&sortBy=price&sortDirection=desc");
        Assert.Equal([9.30m, 8.60m], Prices(third));
        Assert.Equal((3, 5, 12, 3), (third.GetProperty("page").GetInt32(), third.GetProperty("pageSize").GetInt32(),
            third.GetProperty("totalCount").GetInt32(), third.GetProperty("totalPages").GetInt32()));

        var first = await GetPage("?page=1&pageSize=5&sortBy=price&sortDirection=desc");
        Assert.Equal([59.00m, 31.00m, 24.00m, 21.90m, 18.75m], Prices(first));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_keeps_the_totals()
    {
        var page = await GetPage($"?page={int.MaxValue}&pageSize=50");
        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(12, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Equal_sort_keys_page_stably_without_overlap_or_gaps()
    {
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 4; i++)
                db.Products.Add(new Product { Sku = $"TIE-{i}", Name = "Tied", Description = "d", Category = "Test", Price = 5.00m, StockQuantity = 1, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var all = await GetPage("?sortBy=price&sortDirection=asc&pageSize=50");
        var allIds = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        var paged = new List<Guid>();
        for (var p = 1; p <= 16; p++)
        {
            var slice = await GetPage($"?sortBy=price&sortDirection=asc&pageSize=1&page={p}");
            paged.Add(slice.GetProperty("items")[0].GetProperty("id").GetGuid());
        }

        Assert.Equal(16, allIds.Distinct().Count());
        Assert.Equal(allIds, paged);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=51")]
    [InlineData("?sortBy=sku")]
    [InlineData("?sortBy=name;DROP")]
    [InlineData("?sortDirection=up")]
    [InlineData("?page=abc")]
    [InlineData("?pageSize=1.5")]
    public async Task Invalid_paging_or_sorting_parameters_return_400(string query)
    {
        using var response = await _client.GetAsync("/products" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Validation_problem_names_each_bad_parameter()
    {
        using var response = await _client.GetAsync("/products?page=0&sortBy=sku");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("page", out _));
        Assert.True(errors.TryGetProperty("sortBy", out _));
    }

    // Bearer validation runs through the real middleware: only a token this app issued is accepted.
    private static string Token(Action<SecurityTokenDescriptor>? adjust = null, string key = ShopApiFactory.SigningKey)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "MiniShop",
            Audience = "MiniShop.Web",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString() },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        adjust?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private async Task<HttpStatusCode> StatusWith(string? token)
    {
        using var client = fixture.Factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/products");
        return response.StatusCode;
    }

    [Fact]
    public async Task A_token_signed_with_the_configured_key_is_accepted() => Assert.Equal(HttpStatusCode.OK, await StatusWith(Token()));

    [Fact]
    public async Task Missing_token_is_401_with_a_problem_body()
    {
        using var response = await fixture.Client.GetAsync("/products");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Token_with_a_foreign_signature_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(Token(key: "A-Different-Signing-Key-0123456789-abcdef")));

    [Fact]
    public async Task Expired_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(Token(d =>
    {
        d.IssuedAt = DateTime.UtcNow.AddMinutes(-70);
        d.NotBefore = DateTime.UtcNow.AddMinutes(-70);
        d.Expires = DateTime.UtcNow.AddMinutes(-10);
    })));

    [Fact]
    public async Task Token_without_a_usable_subject_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(Token(d => d.Claims["sub"] = "not-a-guid")));

    [Fact]
    public async Task Garbage_token_is_401() => Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith("not.a.jwt"));
}
