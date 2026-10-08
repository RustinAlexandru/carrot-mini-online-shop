using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Common;
using Shop.TestSupport;
using Xunit;

namespace Shop.UnitTests;

public class PersistenceConflictTests
{
    [Fact]
    public void The_helper_builds_a_sql_exception_with_the_requested_number()
        => Assert.Equal(1205, SqlServerErrors.Create(1205).Number);

    [Fact]
    public void A_concurrency_exception_is_a_conflict()
        => Assert.True(PersistenceExceptionHandler.IsConflict(new DbUpdateConcurrencyException("stale")));

    [Fact]
    public void Deadlock_victim_1205_is_a_conflict_bare_or_wrapped_by_ef()
    {
        Assert.True(PersistenceExceptionHandler.IsConflict(SqlServerErrors.Create(1205)));
        Assert.True(PersistenceExceptionHandler.IsConflict(new DbUpdateException("save failed", SqlServerErrors.Create(1205))));
        Assert.True(PersistenceExceptionHandler.IsConflict(new InvalidOperationException("outer", new DbUpdateException("save failed", SqlServerErrors.Create(1205)))));
    }

    [Theory]
    [InlineData(547)] // CHECK/FK violation
    [InlineData(2601)] // unique index
    [InlineData(1222)] // lock timeout, not a deadlock
    public void Other_sql_errors_and_unrelated_exceptions_are_not_conflicts(int number)
    {
        Assert.False(PersistenceExceptionHandler.IsConflict(new DbUpdateException("save failed", SqlServerErrors.Create(number))));
        Assert.False(PersistenceExceptionHandler.IsConflict(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.OrderItems' with unique index 'IX_OrderItems_OrderId_ProductId'.")]
    [InlineData(2627, "Violation of UNIQUE KEY constraint on object 'dbo.OrderItems'.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_OrderItems_Orders_OrderId\".")]
    public void Order_line_unique_and_foreign_key_failures_are_recognized_bare_or_wrapped(int number, string message)
    {
        Assert.True(PersistenceExceptionHandler.IsOrderLineConstraintFailure(SqlServerErrors.Create(number, message)));
        Assert.True(PersistenceExceptionHandler.IsOrderLineConstraintFailure(new DbUpdateException("save failed", SqlServerErrors.Create(number, message))));
        Assert.False(PersistenceExceptionHandler.IsConflict(new DbUpdateException("save failed", SqlServerErrors.Create(number, message))));
    }

    [Theory]
    [InlineData(2601, "Cannot insert duplicate key row in object 'dbo.Products' with unique index 'IX_Products_Sku'.")]
    [InlineData(547, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Orders_Users_UserId\".")]
    [InlineData(1205, "Transaction was deadlocked on OrderItems.")]
    [InlineData(8152, "String or binary data would be truncated in table 'dbo.OrderItems'.")]
    public void Other_tables_and_other_error_numbers_are_not_order_line_constraint_failures(int number, string message)
        => Assert.False(PersistenceExceptionHandler.IsOrderLineConstraintFailure(new DbUpdateException("save failed", SqlServerErrors.Create(number, message))));
}
