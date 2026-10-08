using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shop.Api.Data;
using Shop.Api.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Shop.IntegrationTests;

public static class TestDatabaseGuard
{
    public static void Validate(string connectionString, string environment)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (environment != "Testing" || connection.DataSource != "test-db,1433"
            || connection.InitialCatalog != "ShopTests")
            throw new InvalidOperationException("Test reset requires Testing on test-db,1433 / ShopTests.");
    }
}

/// <summary>Maps an authenticated-only route in the test host so bearer rejection can be asserted without production code.</summary>
public sealed class ProtectedProbeFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints
            .MapGet(ShopApiFactory.ProtectedProbePath, () => Results.Ok(new { status = "authenticated" }))
            .RequireAuthorization());
        next(app);
    };
}

/// <summary>
/// Test-only SaveChanges interceptor that deterministically fails the next save, so the real exception handler
/// can be asserted over HTTP without a timing race. Disarmed by default; production code is unchanged.
/// </summary>
public sealed class SaveFailureInjector : SaveChangesInterceptor
{
    private Exception? _next;
    private Func<Task>? _beforeNext;

    public void FailNextSave(Exception exception) => Interlocked.Exchange(ref _next, exception);
    /// <summary>Runs the callback once, inside the next save and before any SQL is sent: a deterministic stand-in for a concurrent writer.</summary>
    public void BeforeNextSave(Func<Task> concurrentWrite) => Interlocked.Exchange(ref _beforeNext, concurrentWrite);

    public void Disarm()
    {
        Interlocked.Exchange(ref _next, null);
        Interlocked.Exchange(ref _beforeNext, null);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _beforeNext, null) is { } concurrentWrite) await concurrentWrite();
        if (Interlocked.Exchange(ref _next, null) is { } failure) throw failure;
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Test-only command hook: runs one concurrent write just before the next single-order SELECT, i.e. after the sweeper chose its candidates.</summary>
public sealed class CommandHook : DbCommandInterceptor
{
    private Func<Task>? _beforeNextOrderLoad;

    public void BeforeNextOrderLoad(Func<Task> concurrentWrite) => Interlocked.Exchange(ref _beforeNextOrderLoad, concurrentWrite);
    public void Disarm() => Interlocked.Exchange(ref _beforeNextOrderLoad, null);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("TOP(2)", StringComparison.Ordinal) && command.CommandText.Contains("[Orders]", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _beforeNextOrderLoad, null) is { } write)
            await write();
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class ShopApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string SigningKey = "Integration-Tests-Only-Signing-Key-0123456789";
    public const string ProtectedProbePath = "/__test/protected";


    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Shop", connectionString);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        // The automatic sweeper stays off for every test except the dedicated hosted-tick test.
        builder.UseSetting("DraftSweeper:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, ProtectedProbeFilter>();
            services.AddSingleton<SaveFailureInjector>();
            services.AddSingleton<CommandHook>();
            services.ConfigureDbContext<ShopDbContext>((provider, options) => options.AddInterceptors(
                provider.GetRequiredService<SaveFailureInjector>(), provider.GetRequiredService<CommandHook>()));
        });
    }
}

/// <summary>What application startup alone seeded, captured before any test can call ResetAsync.</summary>
public sealed record StartupSeedState(
    int Users, int Products, IReadOnlyList<(string Code, decimal Amount)> Coupons, bool DemoPasswordValid);

public sealed class SqlFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = Environment.GetEnvironmentVariable("SHOP_TEST_CONNECTION")
        ?? throw new InvalidOperationException("SHOP_TEST_CONNECTION is required; integration tests never skip SQL.");
    public ShopApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public StartupSeedState StartupSeed { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        TestDatabaseGuard.Validate(ConnectionString, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "");
        Factory = new ShopApiFactory(ConnectionString);
        // One factory's normal startup owns database initialization for the entire collection.
        Client = Factory.CreateClient();
        StartupSeed = await ReadStartupSeedAsync();
    }

    private async Task<StartupSeedState> ReadStartupSeedAsync()
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var coupons = (await db.Coupons.AsNoTracking().OrderBy(c => c.Code).ToListAsync()).Select(c => (c.Code, c.Amount)).ToList();
        return new StartupSeedState(
            await db.Users.CountAsync(),
            await db.Products.CountAsync(),
            coupons,
            user is not null && user.Email == SeedData.DemoEmail
                && hasher.VerifyHashedPassword(user, user.PasswordHash, SeedData.DemoPassword) == PasswordVerificationResult.Success);
    }

    /// <summary>Deletes every row in FK order, then re-runs the seed. Migration history is preserved.</summary>
    public async Task ResetAsync()
    {
        TestDatabaseGuard.Validate(ConnectionString, Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "");
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        await db.OrderItems.ExecuteDeleteAsync();
        await db.Orders.ExecuteDeleteAsync();
        await db.Products.ExecuteDeleteAsync();
        await db.Coupons.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
        await SeedData.EnsureAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }

    public async Task<string> LoginAsync(string email = SeedData.DemoEmail, string password = SeedData.DemoPassword)
    {
        using var response = await Client.PostAsJsonAsync("/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>A client carrying a bearer token obtained through the real login endpoint.</summary>
    public async Task<HttpClient> AuthenticatedClientAsync(string email = SeedData.DemoEmail, string password = SeedData.DemoPassword)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(email, password));
        return client;
    }

    /// <summary>Adds a second user (the production seed has one) so ownership can be proven; ResetAsync removes it.</summary>
    public async Task<Guid> AddUserAsync(string email, string password)
    {
        await using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var user = new User { Email = email, PasswordHash = "", CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public CommandHook Commands => Factory.Services.GetRequiredService<CommandHook>();

    public SaveFailureInjector SaveFailures => Factory.Services.GetRequiredService<SaveFailureInjector>();

    public AsyncServiceScope CreateScope() => Factory.Services.CreateAsyncScope();

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();
        SqlConnection.ClearAllPools();
    }
}

[CollectionDefinition("SQL", DisableParallelization = true)]
public sealed class SqlCollection : ICollectionFixture<SqlFixture>;
