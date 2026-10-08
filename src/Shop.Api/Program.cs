using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Data;
using Shop.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Shop")
    ?? throw new InvalidOperationException("ConnectionStrings:Shop is required.");
builder.Services.AddDbContext<ShopDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(160)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
var app = builder.Build();
await DatabaseStartup.InitializeAsync(app.Services);
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", DatabaseHealth.CheckAsync);
await app.RunAsync();
public partial class Program;
