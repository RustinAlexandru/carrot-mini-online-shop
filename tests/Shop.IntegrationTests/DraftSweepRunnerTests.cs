using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shop.Api.Data;
using Shop.Api.Domain;
using Shop.Api.Jobs;
using Shop.TestSupport;
using Xunit;

namespace Shop.IntegrationTests;

/// <summary>The sweep through the real runner, EF and SQL Server under a fake clock. TTL 30 minutes, so the cutoff is 11:30.</summary>
[Collection("SQL")]
public sealed class DraftSweepRunnerTests(SqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cutoff = Now.AddMinutes(-30);
    private Guid _userId;
    private Dictionary<string, Product> _products = null!;
    private readonly FakeTimeProvider _time = new(Now);
    private readonly LogSink _logs = new();

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        _userId = (await db.Users.SingleAsync()).Id;
        _products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Sku);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private OrderLine Line(string sku, int quantity)
    {
        var p = _products[sku];
        return new OrderLine(p.Id, p.Sku, p.Name, p.Price, quantity, p.StockQuantity);
    }

    /// <summary>A Draft whose last update was at <paramref name="updatedAt"/>, with two lines and a coupon.</summary>
    private async Task<Guid> Draft(DateTimeOffset updatedAt, DateTimeOffset? expiredAt = null)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var order = Order.Create(_userId, [Line("COF-ETH-250", 2), Line("TEA-EAR-100", 1)], new CouponSnapshot("SAVE5", 5.00m),
            expiredAt is null ? updatedAt : updatedAt.AddHours(-1)).Value;
        if (expiredAt is { } at) Assert.Null(order.Expire(at));
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<SweepResult> Sweep(TimeSpan? ttl = null)
    {
        await using var scope = fixture.CreateScope();
        var runner = new DraftSweepRunner(
            scope.ServiceProvider.GetRequiredService<ShopDbContext>(),
            Options.Create(new DraftSweeperOptions { DraftTtl = ttl ?? TimeSpan.FromMinutes(30) }),
            _time, new CollectingLogger<DraftSweepRunner>(_logs));
        return await runner.RunOnceAsync(CancellationToken.None);
    }

    private async Task<Order> Reload(Guid id)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Orders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == id);
    }

    private static (string Sku, int Quantity)[] Items(Order order) => order.Items.OrderBy(i => i.Sku).Select(i => (i.Sku, i.Quantity)).ToArray();

    [Fact]
    public async Task Old_drafts_expire_and_recent_exact_cutoff_and_already_expired_orders_are_left_alone()
    {
        var old = await Draft(Now.AddHours(-2));
        var justBefore = await Draft(Cutoff.AddTicks(-1));
        var recent = await Draft(Now.AddMinutes(-5));
        var exact = await Draft(Cutoff); // exactly at the cutoff is still live
        var alreadyExpired = await Draft(Now, expiredAt: Now.AddHours(-3));
        var expiredBefore = await Reload(alreadyExpired);

        var result = await Sweep();

        Assert.Equal((2, 2, 0, 0), (result.Scanned, result.Expired, result.Conflicts, result.Skipped));
        Assert.Equal(Cutoff, result.Cutoff);
        foreach (var id in new[] { old, justBefore })
        {
            var order = await Reload(id);
            Assert.Equal((OrderStatus.Expired, Now), (order.Status, order.UpdatedAt));
            Assert.Equal([("COF-ETH-250", 2), ("TEA-EAR-100", 1)], Items(order)); // items are preserved
            Assert.Equal(("SAVE5", 5.00m), (order.CouponCode, order.CouponAmount));
        }
        foreach (var id in new[] { recent, exact })
            Assert.Equal(OrderStatus.Draft, (await Reload(id)).Status);

        var untouched = await Reload(alreadyExpired);
        Assert.Equal((expiredBefore.Status, expiredBefore.UpdatedAt), (untouched.Status, untouched.UpdatedAt));
        Assert.Equal(expiredBefore.Version, untouched.Version);
    }

    [Fact]
    public async Task The_sweep_never_deletes_orders_or_changes_stock()
    {
        await Draft(Now.AddHours(-2));
        await Draft(Now.AddHours(-3));
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            var stockBefore = await db.Products.OrderBy(p => p.Sku).Select(p => new { p.Sku, p.StockQuantity }).ToListAsync();

            await Sweep();

            Assert.Equal((2, 4), (await db.Orders.CountAsync(), await db.OrderItems.CountAsync()));
            Assert.Equal(stockBefore, await db.Products.OrderBy(p => p.Sku).Select(p => new { p.Sku, p.StockQuantity }).ToListAsync());
        }
    }

    [Fact]
    public async Task A_second_sweep_finds_nothing_more_to_do()
    {
        await Draft(Now.AddHours(-2));
        Assert.Equal(1, (await Sweep()).Expired);
        var second = await Sweep();
        Assert.Equal((0, 0), (second.Scanned, second.Expired));
    }

    [Fact]
    public async Task Every_run_logs_one_summary_with_the_counts_including_an_empty_run()
    {
        await Draft(Now.AddHours(-2));
        await Draft(Now.AddMinutes(-1));

        await Sweep();
        await Sweep(); // nothing left to expire

        var lines = _logs.Where(e => e.Message.StartsWith("Draft sweep completed", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Equal((1, 1, 0, 0), Counts(lines[0]));
        Assert.Equal((0, 0, 0, 0), Counts(lines[1]));
        Assert.Equal(Cutoff.ToString("O"), lines[1].Message.Split("cutoff=")[1].Split(' ')[0]);
        Assert.Contains("durationMs=", lines[1].Message);
    }

    private static (int Scanned, int Expired, int Conflicts, int Skipped) Counts(LogEntry e)
        => (Convert.ToInt32(e.State["Scanned"]), Convert.ToInt32(e.State["Expired"]), Convert.ToInt32(e.State["Conflicts"]), Convert.ToInt32(e.State["Skipped"]));

    [Fact]
    public async Task A_failed_run_still_logs_its_summary_as_a_warning_and_throws()
    {
        var unreachable = new SqlConnectionStringBuilder(fixture.ConnectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 1, ConnectRetryCount = 0 };
        await using var db = new ShopDbContext(new DbContextOptionsBuilder<ShopDbContext>().UseSqlServer(unreachable.ConnectionString).Options);
        var runner = new DraftSweepRunner(db, Options.Create(new DraftSweeperOptions()), _time, new CollectingLogger<DraftSweepRunner>(_logs));

        await Assert.ThrowsAnyAsync<Exception>(() => runner.RunOnceAsync(CancellationToken.None));

        var line = Assert.Single(_logs.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.StartsWith("Draft sweep failed", line.Message);
        Assert.Equal((0, 0, 0, 0), Counts(line));
    }

    [Fact]
    public async Task A_draft_edited_after_candidate_selection_is_skipped_not_expired()
    {
        var id = await Draft(Now.AddHours(-2));
        var editedAt = Now.AddMinutes(-1);
        try
        {
            // Fires just before the runner loads the candidate: the owner edits it after the candidate query ran.
            fixture.Commands.BeforeNextOrderLoad(async () =>
            {
                await using var scope = fixture.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
                var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == id);
                Assert.Null(order.Replace([Line("ACC-MUG-12", 4)], null, editedAt));
                await db.SaveLiveOrderAsync(order);
            });
            var result = await Sweep();

            Assert.Equal((1, 0, 0, 1), (result.Scanned, result.Expired, result.Conflicts, result.Skipped));
        }
        finally { fixture.Commands.Disarm(); }

        var after = await Reload(id);
        Assert.Equal((OrderStatus.Draft, editedAt), (after.Status, after.UpdatedAt));
        Assert.Equal([("ACC-MUG-12", 4)], Items(after));
    }

    [Fact]
    public async Task A_draft_edited_between_load_and_save_is_a_counted_conflict_that_keeps_the_edit_and_does_not_stop_the_next()
    {
        var first = await Draft(Now.AddHours(-3)); // oldest, so it is the first candidate saved
        var second = await Draft(Now.AddHours(-2));
        var editedAt = Now.AddMinutes(-1);
        try
        {
            // Fires inside the first candidate's save, after the runner loaded and expired it in memory.
            fixture.SaveFailures.BeforeNextSave(async () =>
            {
                await using var scope = fixture.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
                var order = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == first);
                Assert.Null(order.Replace([Line("ACC-MUG-12", 4)], null, editedAt));
                await db.SaveLiveOrderAsync(order);
            });
            var result = await Sweep();

            Assert.Equal((2, 1, 1, 0), (result.Scanned, result.Expired, result.Conflicts, result.Skipped));
        }
        finally { fixture.SaveFailures.Disarm(); }

        var edited = await Reload(first);
        Assert.Equal((OrderStatus.Draft, editedAt), (edited.Status, edited.UpdatedAt));
        Assert.Equal([("ACC-MUG-12", 4)], Items(edited));
        var swept = await Reload(second);
        Assert.Equal((OrderStatus.Expired, Now), (swept.Status, swept.UpdatedAt));
        Assert.Equal([("COF-ETH-250", 2), ("TEA-EAR-100", 1)], Items(swept));
        Assert.Single(_logs.Where(e => e.Message.StartsWith("Draft sweep completed") && Counts(e) == (2, 1, 1, 0)));
    }

    [Fact]
    public void Options_registered_for_the_shared_test_host_keep_the_automatic_sweeper_off()
        => Assert.False(fixture.Factory.Services.GetRequiredService<IOptions<DraftSweeperOptions>>().Value.Enabled);
}
