using Microsoft.Extensions.Options;

namespace Shop.Api.Jobs;

/// <summary>Runs <see cref="DraftSweepRunner"/> on a PeriodicTimer. Runs are awaited, so they never overlap.</summary>
public sealed class DraftSweeper(
    IServiceScopeFactory scopes, IOptions<DraftSweeperOptions> options, TimeProvider time, ILogger<DraftSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Draft sweeper is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(settings.Interval, time);
        logger.LogInformation("Draft sweeper started: interval={Interval} draftTtl={DraftTtl}", settings.Interval, settings.DraftTtl);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DraftSweepRunner>().RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Draft sweep tick failed; the next tick will run at the usual interval.");
        }
    }
}
