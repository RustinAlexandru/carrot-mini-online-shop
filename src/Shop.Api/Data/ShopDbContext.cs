using Microsoft.EntityFrameworkCore;
namespace Shop.Api.Data;
public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options);
