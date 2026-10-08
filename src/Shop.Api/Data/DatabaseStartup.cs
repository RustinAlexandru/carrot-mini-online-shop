using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Domain;

namespace Shop.Api.Data;

public static class DatabaseStartup
{
    /// <summary>Applies migrations, then inserts missing seed rows; the host accepts requests only afterwards.</summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseStartup));
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            await SeedData.EnsureAsync(
                db,
                scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                cancellationToken);
            logger.LogInformation("Database migrated and seed data ensured.");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Database initialization failed; the API will not start.");
            throw;
        }
    }
}
