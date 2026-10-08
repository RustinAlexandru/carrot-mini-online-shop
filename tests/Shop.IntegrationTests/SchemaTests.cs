using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class SchemaTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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

    // Orders have no public constructor until M3, so the stored constraint is probed with raw SQL.
    private async Task<int?> InsertOrderWithCoupon(string? code, decimal? amount)
    {
        await using var scope = fixture.CreateScope();
        var userId = (await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Users.SingleAsync()).Id;
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Orders (Id, UserId, Status, CreatedAt, UpdatedAt, CouponCode, CouponAmount)
            VALUES (NEWID(), @user, 'Draft', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), @code, @amount)
            """;
        command.Parameters.AddWithValue("@user", userId);
        command.Parameters.Add(new SqlParameter("@code", System.Data.SqlDbType.NVarChar, 32) { Value = (object?)code ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@amount", System.Data.SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = (object?)amount ?? DBNull.Value });
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (SqlException exception)
        {
            return exception.Number;
        }
    }

    private const int CheckConstraintViolation = 547;

    [Theory]
    [InlineData(null, null)]
    [InlineData("SAVE5", "5.00")]
    [InlineData("SAVE10", "10.00")]
    public async Task Order_accepts_no_coupon_snapshot_or_a_complete_positive_one(string? code, string? amount)
        => Assert.Null(await InsertOrderWithCoupon(code, amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("SAVE5", null)]
    [InlineData(null, "5.00")]
    [InlineData("SAVE5", "0.00")]
    [InlineData("SAVE5", "-1.00")]
    [InlineData(null, "0.00")]
    public async Task Order_rejects_half_populated_or_non_positive_coupon_snapshots(string? code, string? amount)
        => Assert.Equal(CheckConstraintViolation, await InsertOrderWithCoupon(code, amount is null ? null : decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));
}
