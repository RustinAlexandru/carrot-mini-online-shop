using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Shop.Api.Common;

/// <summary>Maps stale-version and deadlock-victim failures to the same reload/retry 409, however deeply EF wrapped them.</summary>
public sealed class PersistenceExceptionHandler : IExceptionHandler
{
    public const int SqlDeadlockVictim = 1205;
    public const string StaleOrderMessage = "The order was changed by another request. Reload it and try again.";

    public static bool IsConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException or SqlException { Number: SqlDeadlockVictim })
                return true;
        return false;
    }

    public const int SqlUniqueIndexViolation = 2601;
    public const int SqlUniqueConstraintViolation = 2627;
    public const int SqlForeignKeyViolation = 547;

    /// <summary>
    /// True for a unique-index or foreign-key failure raised by an OrderItems row. A stale writer that only adds a line can hit one
    /// of these before the guarded Orders UPDATE runs, so the caller must still verify staleness before calling it a conflict.
    /// </summary>
    public static bool IsOrderLineConstraintFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation or SqlForeignKeyViolation } sql
                && sql.Message.Contains("OrderItems", StringComparison.Ordinal))
                return true;
        return false;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsConflict(exception)) return false;
        var conflict = new AppError.Conflict(StaleOrderMessage);
        await ApiProblems.ToResult(conflict).ExecuteAsync(httpContext);
        return true;
    }
}
