using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class SchemaTests(SqlFixture fixture)
{
    [Fact]
    public async Task Startup_applies_the_initial_migration_and_the_model_has_no_pending_changes()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.Contains("InitialShopSchema", (await db.Database.GetAppliedMigrationsAsync()).Single());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges(), "Model changed without a new migration.");
    }

    [Fact]
    public async Task Database_uses_the_case_insensitive_collation_and_all_tables()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128)),
                   (SELECT COUNT(*) FROM sys.tables WHERE name IN ('Users','Products','Coupons','Orders','OrderItems'))
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(ShopDbContext.Collation, reader.GetString(0));
        Assert.Equal(5, reader.GetInt32(1));
    }
}
