using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Api.Domain;

namespace Shop.Api.Data.Configurations;

public sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> item)
    {
        item.ToTable(t => t.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0"));
        item.HasKey(i => i.Id);
        item.Property(i => i.Sku).HasMaxLength(64).IsRequired();
        item.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
        item.Property(i => i.UnitPrice).HasPrecision(18, 2);
        item.HasOne<Product>().WithMany().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.NoAction);
        item.HasIndex(i => new { i.OrderId, i.ProductId }).IsUnique();
    }
}
