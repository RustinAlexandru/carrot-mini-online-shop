using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Data;
using Shop.Api.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Shop.IntegrationTests;

public static class TestDatabaseGuard
{
    public static void Validate(string connectionString, string environment)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (environment != "Testing" || connection.DataSource != "test-db,1433"
            || connection.InitialCatalog != "ShopTests")
            throw new InvalidOperationException("Test reset requires Testing on test-db,1433 / ShopTests.");
    }
}

/// <summary>Maps an authenticated-only route in the test host so bearer rejection can be asserted without production code.</summary>
public sealed class ProtectedProbeFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints
            .MapGet(ShopApiFactory.ProtectedProbePath, () => Results.Ok(new { status = "authenticated" }))
            .RequireAuthorization());
        next(app);
    };
}

public sealed class ShopApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string SigningKey = "Integration-Tests-Only-Signing-Key-0123456789";
    public const string ProtectedProbePath = "/__test/protected";


    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Shop", connectionString);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ProtectedProbeFilter>());
    }
}

public sealed class SqlFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("SHOP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("SHOP_TEST_CONNECTION is required; integration tests never skip SQL.");
    public ShopApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        TestDatabaseGuard.Validate(ConnectionString, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "");
        Factory = new ShopApiFactory(ConnectionString);
        // One factory's normal startup owns database initialization for the entire collection.
        Client = Factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>Deletes every row in FK order, then re-runs the seed. Migration history is preserved.</summary>
    public async Task ResetAsync()
    {
        TestDatabaseGuard.Validate(ConnectionString, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "");
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        await db.OrderItems.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.Coupons.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
        await SeedData.EnsureAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }

    public async Task<string> LoginAsync(string email = SeedData.DemoEmail, string password = SeedData.DemoPassword)
    {
        using var response = await Client.PostAsJsonAsync("/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>A client carrying a bearer token obtained through the real login endpoint.</summary>
    public async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync());
        return client;
    }

    public AsyncServiceScope CreateScope() => Factory.Services.CreateAsyncScope();

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        SqlConnection.ClearAllPools();
    }
}

[CollectionDefinition("SQL", DisableParallelization = true)]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>;
