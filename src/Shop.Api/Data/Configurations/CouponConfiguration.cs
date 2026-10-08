using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Api.Domain;

namespace Shop.Api.Data.Configurations;

public sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> coupon)
    {
        coupon.ToTable(t => t.HasCheckConstraint("CK_Coupons_Amount", "[Amount] > 0"));
        coupon.HasKey(c => c.Id);
        coupon.Property(c => c.Code).HasMaxLength(32).IsRequired();
        coupon.Property(c => c.Amount).HasPrecision(18, 2);
        coupon.HasIndex(c => c.Code).IsUnique();
    }
}
