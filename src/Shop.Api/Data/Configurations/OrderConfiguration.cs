using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Api.Domain;

namespace Shop.Api.Data.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> order)
    {
        order.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Orders_CouponSnapshot",
                "([CouponCode] IS NULL AND [CouponAmount] IS NULL) OR ([CouponCode] IS NOT NULL AND [CouponAmount] IS NOT NULL AND [CouponAmount] > 0)");
        });
        order.HasKey(o => o.Id);
        order.Property(o => o.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        order.Property(o => o.CouponCode).HasMaxLength(32);
        order.Property(o => o.CouponAmount).HasPrecision(18, 2);
        order.Property(o => o.Version).IsRowVersion();
        order.HasOne<User>().WithMany().HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.NoAction);
        order.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        order.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        order.HasIndex(o => new { o.UserId, o.CreatedAt });
        order.HasIndex(o => new { o.Status, o.UpdatedAt });
    }
}
