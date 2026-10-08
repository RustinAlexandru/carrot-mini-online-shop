using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shop.Api.Jobs;
using Shop.TestSupport;
using Xunit;

namespace Shop.UnitTests;

public class DraftSweeperTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DraftSweeperOptionsValidator Validator = new();

    // ---- options ----

    [Fact]
    public void Defaults_are_enabled_one_minute_and_thirty_minutes_and_valid()
    {
        var options = new DraftSweeperOptions();
        Assert.Equal((true, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30)), (options.Enabled, options.Interval, options.DraftTtl));
        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:00", "00:30:00", "DraftSweeper:Interval")]
    [InlineData("-00:01:00", "00:30:00", "DraftSweeper:Interval")]
    [InlineData("00:01:00", "00:00:00", "DraftSweeper:DraftTtl")]
    [InlineData("00:01:00", "-00:05:00", "DraftSweeper:DraftTtl")]
    public void Non_positive_intervals_and_ttls_are_rejected(string interval, string ttl, string expected)
    {
        var result = Validator.Validate(null, new DraftSweeperOptions { Interval = TimeSpan.Parse(interval), DraftTtl = TimeSpan.Parse(ttl) });
        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }

    private static IHost CreateHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start));
        builder.Services.AddDraftSweeper();
        return builder.Build();
    }

    [Theory]
    [InlineData("DraftSweeper:Interval", "00:00:00", "DraftSweeper:Interval")]
    [InlineData("DraftSweeper:DraftTtl", "-00:01:00", "DraftSweeper:DraftTtl")]
    public async Task Invalid_configuration_fails_host_start_even_when_the_sweeper_is_disabled(string key, string value, string expected)
    {
        using var host = CreateHost(new() { [key] = value, ["DraftSweeper:Enabled"] = "false" });
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task Configuration_binds_enabled_interval_and_ttl_from_settings()
    {
        using var host = CreateHost(new() { ["DraftSweeper:Enabled"] = "false", ["DraftSweeper:Interval"] = "00:00:07", ["DraftSweeper:DraftTtl"] = "00:12:00" });
        await host.StartAsync();
        var options = host.Services.GetRequiredService<IOptions<DraftSweeperOptions>>().Value;
        Assert.Equal((false, TimeSpan.FromSeconds(7), TimeSpan.FromMinutes(12)), (options.Enabled, options.Interval, options.DraftTtl));
        await host.StopAsync();
    }

    // ---- the background loop (no database: the scope factory is a stand-in) ----

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public int Calls;
        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("boom");
        }
    }

    private static DraftSweeper Sweeper(IServiceScopeFactory scopes, FakeTimeProvider clock, LogSink sink, bool enabled = true)
        => new(scopes, Options.Create(new DraftSweeperOptions { Enabled = enabled, Interval = TimeSpan.FromMinutes(1) }), clock, new CollectingLogger<DraftSweeper>(sink));

    [Fact]
    public async Task A_failing_tick_is_logged_with_its_exception_and_the_loop_keeps_ticking()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new LogSink();
        var scopes = new ThrowingScopeFactory();
        using var sweeper = Sweeper(scopes, clock, sink);
        await sweeper.StartAsync(CancellationToken.None);
        await sink.WaitForAsync(e => e.Message.StartsWith("Draft sweeper started"));

        clock.Advance(TimeSpan.FromMinutes(1));
        var failures = await sink.WaitForAsync(e => e.Level == LogLevel.Error, 1);
        Assert.IsType<InvalidOperationException>(failures[0].Exception);

        clock.Advance(TimeSpan.FromMinutes(1));
        await sink.WaitForAsync(e => e.Level == LogLevel.Error, 2);
        Assert.Equal(2, scopes.Calls);
        await sweeper.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Shutdown_cancellation_ends_the_loop_promptly_without_logging_an_error()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new LogSink();
        var scopes = new ThrowingScopeFactory();
        using var sweeper = Sweeper(scopes, clock, sink);
        await sweeper.StartAsync(CancellationToken.None);
        await sink.WaitForAsync(e => e.Message.StartsWith("Draft sweeper started"));

        await sweeper.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(sweeper.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(0, scopes.Calls);
        Assert.Empty(sink.Where(e => e.Level >= LogLevel.Warning));
    }

    [Fact]
    public async Task A_disabled_sweeper_never_ticks()
    {
        var clock = new FakeTimeProvider(Start);
        var sink = new LogSink();
        var scopes = new ThrowingScopeFactory();
        using var sweeper = Sweeper(scopes, clock, sink, enabled: false);
        await sweeper.StartAsync(CancellationToken.None);
        await sink.WaitForAsync(e => e.Message == "Draft sweeper is disabled.");

        clock.Advance(TimeSpan.FromHours(1));
        await sweeper.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, scopes.Calls);
        await sweeper.StopAsync(CancellationToken.None);
    }
}
