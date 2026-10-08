using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Api.Domain;

namespace Shop.Api.Data.Configurations;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> product)
    {
        product.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Products_Price", "[Price] >= 0 AND [Price] <= 999999999.99");
            t.HasCheckConstraint("CK_Products_StockQuantity", "[StockQuantity] >= 0");
        });
        product.HasKey(p => p.Id);
        product.Property(p => p.Sku).HasMaxLength(64).IsRequired();
        product.Property(p => p.Name).HasMaxLength(200).IsRequired();
        product.Property(p => p.Description).HasMaxLength(2000).IsRequired();
        product.Property(p => p.Category).HasMaxLength(80).IsRequired();
        product.Property(p => p.Price).HasPrecision(18, 2);
        product.HasIndex(p => p.Sku).IsUnique();
    }
}
