using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
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

public sealed class ShopApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Shop", connectionString);
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
