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

    // Deliberately not the 1 minute / 30 minute defaults: only honouring the configured values passes this test.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(7);

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
            builder.UseSetting("DraftSweeper:Interval", "00:00:07");
            builder.UseSetting("DraftSweeper:DraftTtl", "00:00:21");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
            });
            builder.ConfigureLogging(logging => logging.AddProvider(new CollectingLoggerProvider(sink)));
        });
        using var client = factory.CreateClient(); // starts the host and with it the real DraftSweeper
        await sink.WaitForAsync(e => e.Message.StartsWith("Draft sweeper started: interval=00:00:07 draftTtl=00:00:21", StringComparison.Ordinal));

        // The PeriodicTimer exists now. Nothing runs until the fake clock reaches the configured interval.
        Assert.Empty(sink.Where(IsSummary));

        // Tick 1 (+7s, cutoff -14s): the hour-old draft expires, the fresh one lives.
        clock.Advance(Interval);
        var ticks = await sink.WaitForAsync(IsSummary, 1);
        Assert.Equal((1, 1), Counts(ticks[0]));
        Assert.Equal(Start - 2 * Interval, (DateTimeOffset)ticks[0].State["Cutoff"]!);
        var swept = await Reload(old);
        Assert.Equal((OrderStatus.Expired, Start + Interval), (swept.Status, swept.UpdatedAt));
        Assert.Equal(OrderStatus.Draft, (await Reload(fresh)).Status);

        // Tick 2 (+14s) finds nothing; tick 3 (+21s) has cutoff == the fresh order's UpdatedAt, so it is still live.
        clock.Advance(Interval);
        ticks = await sink.WaitForAsync(IsSummary, 2);
        Assert.Equal((0, 0), Counts(ticks[1]));
        clock.Advance(Interval);
        ticks = await sink.WaitForAsync(IsSummary, 3);
        Assert.Equal((0, 0), Counts(ticks[2]));
        Assert.Equal(OrderStatus.Draft, (await Reload(fresh)).Status);

        // Tick 4 (+28s, cutoff +7s): now older than the 21 second TTL.
        clock.Advance(Interval);
        ticks = await sink.WaitForAsync(IsSummary, 4);
        Assert.Equal((1, 1), Counts(ticks[3]));
        var expired = await Reload(fresh);
        Assert.Equal((OrderStatus.Expired, Start + 4 * Interval), (expired.Status, expired.UpdatedAt));
        Assert.Single(expired.Items); // expiry never removes lines
        Assert.Equal(4, sink.Where(IsSummary).Count);
    }
}
