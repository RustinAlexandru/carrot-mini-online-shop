using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Data;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class ShellTests(SqlFixture fixture)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task Static_shell_is_served_as_html(string path)
    {
        using var response = await fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Mini Online Shop</title>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sql_connection_targets_the_isolated_application_database()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME()";
        Assert.Equal("ShopTests", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Health_endpoint_checks_the_real_database()
    {
        using var response = await fixture.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_check_returns_service_unavailable_when_sql_cannot_connect()
    {
        // Closed local port fails promptly without changing the shared SQL fixture or its startup.
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            DataSource = "tcp:127.0.0.1,1",
            ConnectTimeout = 1,
            ConnectRetryCount = 0
        };
        var options = new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(connection.ConnectionString).Options;
        await using var db = new ShopDbContext(options);
        var result = await DatabaseHealth.CheckAsync(db, CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("db,1433", "Shop", "Testing")]
    [InlineData("test-db,1433", "Shop", "Testing")]
    [InlineData("db,1433", "ShopTests", "Testing")]
    [InlineData("test-db,1433", "ShopTests", "Production")]
    public void Reset_guard_refuses_runtime_or_non_testing_targets(string host, string database, string environment)
    {
        var connection = new SqlConnectionStringBuilder { DataSource = host, InitialCatalog = database };
        Assert.Throws<InvalidOperationException>(() => TestDatabaseGuard.Validate(connection.ConnectionString, environment));
    }

    [Fact]
    public void Reset_guard_accepts_only_the_fixed_test_target()
        => TestDatabaseGuard.Validate(fixture.ConnectionString, "Testing");
}
