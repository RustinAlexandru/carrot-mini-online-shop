using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Shop.Api.Common;

/// <summary>Maps stale-version and deadlock-victim failures to the same reload/retry 409, however deeply EF wrapped them.</summary>
public sealed class PersistenceExceptionHandler : IExceptionHandler
{
    public const int SqlDeadlockVictim = 1205;

    public static bool IsConflict(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException or SqlException { Number: SqlDeadlockVictim })
                return true;
        return false;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsConflict(exception)) return false;
        var conflict = new AppError.Conflict("The order was changed by another request. Reload it and try again.");
        await ApiProblems.ToResult(conflict).ExecuteAsync(httpContext);
        return true;
    }
}
