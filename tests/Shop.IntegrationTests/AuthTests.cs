using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Shop.Api.Data;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class AuthTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Login_returns_a_signed_token_for_the_seeded_user()
    {
        using var response = await fixture.Client.PostAsJsonAsync("/auth/login", new { email = SeedData.DemoEmail, password = SeedData.DemoPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = new JsonWebTokenHandler().ReadJsonWebToken(body.RootElement.GetProperty("token").GetString());
        await using var scope = fixture.CreateScope();
        var userId = (await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Users.SingleAsync()).Id;
        Assert.Equal(userId.ToString(), token.Subject);
        Assert.Equal("MiniShop", token.Issuer);
        Assert.Equal(["MiniShop.Web"], token.Audiences);
        Assert.Equal("HS256", token.Alg);
        var expiresAt = body.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.Equal(new DateTimeOffset(token.ValidTo, TimeSpan.Zero), expiresAt);
        Assert.InRange(expiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }

    [Theory]
    [InlineData("demo@shop.test")]
    [InlineData("  Demo@Shop.TEST ")]
    public async Task Login_ignores_email_case_and_surrounding_spaces(string email)
    {
        using var response = await fixture.Client.PostAsJsonAsync("/auth/login", new { email, password = SeedData.DemoPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401_problem()
    {
        using var wrongPassword = await fixture.Client.PostAsJsonAsync("/auth/login", new { email = SeedData.DemoEmail, password = "nope" });
        using var unknownEmail = await fixture.Client.PostAsJsonAsync("/auth/login", new { email = "ghost@shop.test", password = SeedData.DemoPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        using var first = JsonDocument.Parse(await wrongPassword.Content.ReadAsStringAsync());
        using var second = JsonDocument.Parse(await unknownEmail.Content.ReadAsStringAsync());
        foreach (var field in new[] { "status", "title", "detail" })
            Assert.Equal(first.RootElement.GetProperty(field).ToString(), second.RootElement.GetProperty(field).ToString());
        Assert.Equal(401, first.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Invalid email or password.", first.RootElement.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("""{"email":"","password":"x"}""", "email")]
    [InlineData("""{"email":"demo@shop.test"}""", "password")]
    [InlineData("""{"email":"demo@shop.test","password":"x","role":"admin"}""", "")]
    [InlineData("""{"email":""", "")]
    public async Task Invalid_login_bodies_get_a_400(string json, string field)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/auth/login", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (field != "")
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }
    }
}
