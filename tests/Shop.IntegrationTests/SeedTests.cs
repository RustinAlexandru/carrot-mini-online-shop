using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Data;
using Shop.Api.Domain;
using Xunit;

namespace Shop.IntegrationTests;

[Collection("SQL")]
public sealed class SeedTests(SqlFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Task Seed(IServiceProvider services, ShopDbContext db)
        => SeedData.EnsureAsync(db, services.GetRequiredService<IPasswordHasher<User>>(), services.GetRequiredService<TimeProvider>());

    [Fact]
    public async Task Seed_creates_one_user_twelve_products_and_two_coupons()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var user = await db.Users.SingleAsync();
        Assert.Equal(SeedData.DemoEmail, user.Email);
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, user.PasswordHash, SeedData.DemoPassword));
        var products = await db.Products.ToListAsync();
        Assert.Equal(12, products.Count);
        Assert.Equal(3, products.Select(p => p.Category).Distinct().Count());
        Assert.Contains(products, p => p.StockQuantity == 0);
        Assert.Contains(products, p => p.StockQuantity == 3);
        Assert.Equal(
            [("SAVE10", 10.00m), ("SAVE5", 5.00m)],
            (await db.Coupons.OrderBy(c => c.Code).ToListAsync()).Select(c => (c.Code, c.Amount)));
    }

    [Fact]
    public async Task Seeding_twice_without_reset_keeps_identical_rows_and_password_hash()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var before = await Snapshot(db);

        await Seed(scope.ServiceProvider, db);
        await Seed(scope.ServiceProvider, db);

        Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task Seed_inserts_only_missing_rows_and_preserves_existing_ones()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var kept = await db.Products.SingleAsync(p => p.Sku == "COF-ETH-250");
        var keptId = kept.Id;
        kept.Price = 99.99m;
        kept.StockQuantity = 7;
        db.Coupons.Remove(await db.Coupons.SingleAsync(c => c.Code == "SAVE5"));
        db.Products.Remove(await db.Products.SingleAsync(p => p.Sku == "TEA-EAR-100"));
        await db.SaveChangesAsync();

        await Seed(scope.ServiceProvider, db);

        await using var verify = fixture.CreateScope();
        var fresh = verify.ServiceProvider.GetRequiredService<ShopDbContext>();
        var product = await fresh.Products.SingleAsync(p => p.Sku == "COF-ETH-250");
        Assert.Equal((keptId, 99.99m, 7), (product.Id, product.Price, product.StockQuantity));
        Assert.Equal(12, await fresh.Products.CountAsync());
        Assert.Equal(5.00m, (await fresh.Coupons.SingleAsync(c => c.Code == "SAVE5")).Amount);
        Assert.Equal(1, await fresh.Users.CountAsync());
    }

    private static async Task<string> Snapshot(ShopDbContext db)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).Select(u => $"{u.Id}|{u.Email}|{u.PasswordHash}").ToListAsync();
        var products = await db.Products.AsNoTracking().OrderBy(p => p.Sku).Select(p => $"{p.Id}|{p.Sku}|{p.Price}|{p.StockQuantity}|{p.CreatedAt:O}").ToListAsync();
        var coupons = await db.Coupons.AsNoTracking().OrderBy(c => c.Code).Select(c => $"{c.Id}|{c.Code}|{c.Amount}").ToListAsync();
        return string.Join('\n', users.Concat(products).Concat(coupons));
    }
}
