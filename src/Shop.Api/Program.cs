using Microsoft.EntityFrameworkCore;
using Shop.Api.Data;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Shop")
    ?? throw new InvalidOperationException("ConnectionStrings:Shop is required.");
builder.Services.AddDbContext<ShopDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(160)));
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    // The M1 shell has no entity migrations yet; M2 adds the initial schema and seed.
    var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
    await db.Database.MigrateAsync();
}
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", async (ShopDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
await app.RunAsync();
public partial class Program;
