using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shop.Api.Data;
using Shop.Api.Domain;
using Shop.TestSupport;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>The one test that runs the real registered hosted service: a derived host over the same test database with the sweeper enabled and a fake clock.</summary>
[Collection("SQL")]
public sealed class DraftSweeperHostedTests(SqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> Draft(DateTimeOffset updatedAt)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var user = await db.Users.SingleAsync();
        var product = await db.Products.FirstAsync(p => p.Sku == "COF-ETH-250");
        var order = Order.Create(user.Id, [new OrderLine(product.Id, product.Sku, product.Name, product.Price, 1, product.StockQuantity)], null, updatedAt).Value;
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<Order> Reload(Guid id)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == id);
    }

    private static bool IsSummary(LogEntry e) => e.Message.StartsWith("Draft sweep completed", StringComparison.Ordinal);

    private static (int Scanned, int Expired) Counts(LogEntry e) => (Convert.ToInt32(e.State["Scanned"]), Convert.ToInt32(e.State["Expired"]));

    [Fact]
    public async Task The_registered_hosted_service_ticks_at_the_configured_interval_and_expires_by_the_configured_ttl()
    {
        var old = await Draft(Start.AddHours(-1));
        var fresh = await Draft(Start);
        var clock = new FakeTimeProvider(Start);
        var sink = new LogSink();

        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DraftSweeper:Enabled", "true");
            builder.UseSetting("DraftSweeper:Interval", "00:01:00");
            builder.UseSetting("DraftSweeper:DraftTtl", "00:03:00");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new CollectingLoggerProvider(sink)));
        });
        using var client = factory.CreateClient(); // starts the host and with it the real DraftSweeper
        await sink.WaitForAsync(e => e.Message.StartsWith("Draft sweeper started: interval=00:01:00 draftTtl=00:03:00", StringComparison.Ordinal));

        // The PeriodicTimer exists now. Nothing runs until the fake clock reaches the configured interval.
        Assert.Empty(sink.Where(IsSummary));

        // Tick 1 (+1m, cutoff -2m): the hour-old draft expires, the fresh one lives.
        clock.Advance(TimeSpan.FromMinutes(1));
        var ticks = await sink.WaitForAsync(IsSummary, 1);
        Assert.Equal((1, 1), Counts(ticks[0]));
        Assert.Equal(Start.AddMinutes(-2), (DateTimeOffset)ticks[0].State["Cutoff"]!);
        var swept = await Reload(old);
        Assert.Equal((OrderStatus.Expired, Start.AddMinutes(1)), (swept.Status, swept.UpdatedAt));
        Assert.Equal(OrderStatus.Draft, (await Reload(fresh)).Status);

        // Tick 2 (+2m) finds nothing; tick 3 (+3m) has cutoff == the fresh order's UpdatedAt, so it is still live.
        clock.Advance(TimeSpan.FromMinutes(1));
        ticks = await sink.WaitForAsync(IsSummary, 2);
        Assert.Equal((0, 0), Counts(ticks[1]));
        clock.Advance(TimeSpan.FromMinutes(1));
        ticks = await sink.WaitForAsync(IsSummary, 3);
        Assert.Equal((0, 0), Counts(ticks[2]));
        Assert.Equal(OrderStatus.Draft, (await Reload(fresh)).Status);

        // Tick 4 (+4m, cutoff +1m): now older than the 3 minute TTL.
        clock.Advance(TimeSpan.FromMinutes(1));
        ticks = await sink.WaitForAsync(IsSummary, 4);
        Assert.Equal((1, 1), Counts(ticks[3]));
        var expired = await Reload(fresh);
        Assert.Equal((OrderStatus.Expired, Start.AddMinutes(4)), (expired.Status, expired.UpdatedAt));
        Assert.Single(expired.Items); // expiry never removes lines
        Assert.Equal(4, sink.Where(IsSummary).Count);
    }
}
