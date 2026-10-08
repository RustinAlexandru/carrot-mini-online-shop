using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shop.Api.Common;
using Shop.Api.Data;
using Shop.Api.Domain;

namespace Shop.Api.Jobs;

public sealed record SweepResult(DateTimeOffset Cutoff, int Scanned, int Expired, int Conflicts, int Skipped, TimeSpan Duration);

/// <summary>
/// One sweep. It only selects likely candidates; whether an order is abandoned and how it expires is decided by the
/// Order aggregate on the freshly loaded state. It never deletes and never touches stock.
/// </summary>
public sealed class DraftSweepRunner(
    ShopDbContext db, IOptions<DraftSweeperOptions> options, TimeProvider time, ILogger<DraftSweepRunner> logger)
{
    public async Task<SweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var now = time.GetUtcNow();
        var cutoff = now - options.Value.DraftTtl;
        int scanned = 0, expired = 0, conflicts = 0, skipped = 0;
        var failed = true;
        try
        {
            var candidates = await db.Orders.AsNoTracking()
                .Where(o => o.Status == OrderStatus.Draft && o.UpdatedAt < cutoff)
                .OrderBy(o => o.UpdatedAt).ThenBy(o => o.Id) // oldest first, deterministic
                .Select(o => o.Id).ToListAsync(cancellationToken);
            scanned = candidates.Count;

            foreach (var id in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (await ExpireAsync(id, cutoff, now, cancellationToken))
                {
                    case Outcome.Expired: expired++; break;
                    case Outcome.Conflict: conflicts++; break;
                    default: skipped++; break;
                }
            }

            failed = false;
            return Summarize();
        }
        finally
        {
            // One structured line per run, including empty and failed runs. A failure's exception is logged by the caller.
            var summary = Summarize();
            if (failed)
                logger.LogWarning("Draft sweep failed: cutoff={Cutoff:O} scanned={Scanned} expired={Expired} conflicts={Conflicts} skipped={Skipped} durationMs={DurationMs}",
                    summary.Cutoff, summary.Scanned, summary.Expired, summary.Conflicts, summary.Skipped, (long)summary.Duration.TotalMilliseconds);
            else
                logger.LogInformation("Draft sweep completed: cutoff={Cutoff:O} scanned={Scanned} expired={Expired} conflicts={Conflicts} skipped={Skipped} durationMs={DurationMs}",
                    summary.Cutoff, summary.Scanned, summary.Expired, summary.Conflicts, summary.Skipped, (long)summary.Duration.TotalMilliseconds);
        }

        SweepResult Summarize() => new(cutoff, scanned, expired, conflicts, skipped, time.GetElapsedTime(started));
    }

    private enum Outcome { Expired, Conflict, Skipped }

    private async Task<Outcome> ExpireAsync(Guid id, DateTimeOffset cutoff, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
            // Deleted, edited or already expired since selection: the loaded aggregate decides, not the stale candidate query.
            if (order is null || !order.IsAbandoned(cutoff)) return Outcome.Skipped;
            if (order.Expire(now) is not null) return Outcome.Skipped;

            await db.SaveLiveOrderAsync(order, cancellationToken);
            return Outcome.Expired;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && PersistenceExceptionHandler.IsConflict(exception))
        {
            return Outcome.Conflict;
        }
        finally
        {
            db.ChangeTracker.Clear(); // a failed candidate must not poison the next save
        }
    }
}
