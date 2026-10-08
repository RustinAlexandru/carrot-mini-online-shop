using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shop.Api.Domain;

namespace Shop.Api.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> user)
    {
        user.HasKey(u => u.Id);
        user.Property(u => u.Email).HasMaxLength(254).IsRequired();
        user.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
        user.Property(u => u.CreatedAt).IsRequired();
        user.HasIndex(u => u.Email).IsUnique();
    }
}
