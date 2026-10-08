using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>
/// Bearer validation through the real middleware, against a protected route that exists only in the test host.
/// M3 repeats these assertions on the real order routes.
/// </summary>
[Collection("SQL")]
public sealed class BearerTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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
        using var response = await client.GetAsync(ShopApiFactory.ProtectedProbePath);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_token_signed_with_the_configured_key_is_accepted() => Assert.Equal(HttpStatusCode.OK, await StatusWith(Token()));

    [Fact]
    public async Task A_token_from_the_real_login_endpoint_is_accepted() => Assert.Equal(HttpStatusCode.OK, await StatusWith(await fixture.LoginAsync()));

    [Fact]
    public async Task Missing_token_is_401_with_a_bearer_challenge()
    {
        // The probe route sits ahead of the app's status-code pages, so the Problem Details body is asserted on real routes in M3.
        using var response = await fixture.Client.GetAsync(ShopApiFactory.ProtectedProbePath);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
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
