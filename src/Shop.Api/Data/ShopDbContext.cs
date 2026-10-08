using Microsoft.EntityFrameworkCore;
using Shop.Api.Domain;

namespace Shop.Api.Data;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";

    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    /// <summary>
    /// The one save path for a tracked, live order. Forcing UpdatedAt modified makes EF emit a rowversion-guarded
    /// UPDATE of the header even when only child rows changed or the supplied timestamp equals the old one,
    /// so every mutation is a single atomic SaveChangesAsync that a stale version aborts as a whole.
    /// </summary>
    public Task<int> SaveLiveOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        Entry(order).Property(o => o.UpdatedAt).IsModified = true;
        return SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.UseCollation(Collation);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShopDbContext).Assembly);
    }
}
