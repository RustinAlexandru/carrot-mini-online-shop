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
}
