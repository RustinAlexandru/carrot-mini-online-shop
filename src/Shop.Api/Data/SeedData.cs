using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Domain;

namespace Shop.Api.Data;

public static class SeedData
{
    public const string DemoEmail = "demo@shop.test";
    public const string DemoPassword = "DemoShop123!";

    private static readonly DateTimeOffset CatalogEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record ProductSeed(string Sku, string Name, string Category, decimal Price, int Stock, string Description);

    private static readonly ProductSeed[] Products =
    [
        new("COF-ETH-250", "Ethiopia Yirgacheffe 250g", "Coffee", 14.50m, 40, "Washed single origin with jasmine and citrus notes."),
        new("COF-COL-250", "Colombia Huila 250g", "Coffee", 12.90m, 35, "Balanced medium roast with caramel and red apple."),
        new("COF-BRA-1KG", "Brazil Santos 1kg", "Coffee", 31.00m, 3, "Low-acidity nutty beans in a bulk bag; nearly sold out."),
        new("COF-ESP-500", "House Espresso Blend 500g", "Coffee", 18.75m, 60, "Dark chocolate body built for milk drinks."),
        new("COF-DEC-250", "Swiss Water Decaf 250g", "Coffee", 13.40m, 0, "Chemical-free decaf; currently out of stock."),
        new("TEA-SEN-100", "Japanese Sencha 100g", "Tea", 11.20m, 50, "Fresh grassy green tea steamed in Shizuoka."),
        new("TEA-EAR-100", "Earl Grey 100g", "Tea", 8.60m, 80, "Black tea scented with natural bergamot oil."),
        new("TEA-CHA-100", "Masala Chai 100g", "Tea", 9.30m, 45, "Assam with cardamom, ginger and cinnamon."),
        new("TEA-MAT-030", "Ceremonial Matcha 30g", "Tea", 24.00m, 25, "Stone-ground first-harvest matcha."),
        new("ACC-POUR-01", "Ceramic Pour-Over Dripper", "Accessories", 21.90m, 30, "Size 02 cone dripper with ribbed walls."),
        new("ACC-GRIND-01", "Burr Hand Grinder", "Accessories", 59.00m, 12, "Stainless conical burrs with a 20-click dial."),
        new("ACC-MUG-12", "Stoneware Mug 12oz", "Accessories", 9.90m, 100, "Dishwasher-safe mug with a matte glaze.")
    ];

    private static readonly (string Code, decimal Amount)[] Coupons = [("SAVE5", 5.00m), ("SAVE10", 10.00m)];

    /// <summary>Inserts whatever seed rows are missing, keyed by email/SKU/code, and never touches existing rows.</summary>
    public static async Task EnsureAsync(
        ShopDbContext db, IPasswordHasher<User> hasher, TimeProvider time, CancellationToken cancellationToken = default)
    {
        var emails = await db.Users.Select(u => u.Email).ToListAsync(cancellationToken);
        if (!emails.Contains(DemoEmail, StringComparer.OrdinalIgnoreCase))
        {
            var demo = new User { Email = DemoEmail, PasswordHash = "", CreatedAt = time.GetUtcNow() };
            demo.PasswordHash = hasher.HashPassword(demo, DemoPassword);
            db.Users.Add(demo);
        }

        var skus = (await db.Products.Select(p => p.Sku).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Products.Length; i++)
        {
            var seed = Products[i];
            if (skus.Contains(seed.Sku)) continue;
            var created = CatalogEpoch.AddDays(i);
            db.Products.Add(new Product
            {
                Sku = seed.Sku, Name = seed.Name, Description = seed.Description, Category = seed.Category,
                Price = seed.Price, StockQuantity = seed.Stock, CreatedAt = created, UpdatedAt = created
            });
        }

        var codes = (await db.Coupons.Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, amount) in Coupons)
            if (!codes.Contains(code))
                db.Coupons.Add(new Coupon { Code = code, Amount = amount });

        await db.SaveChangesAsync(cancellationToken);
    }
}
