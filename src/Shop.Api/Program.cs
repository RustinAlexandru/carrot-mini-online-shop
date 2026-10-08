using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Data;
using Shop.Api.Common;
using Shop.Api.Features.Auth;
using Shop.Api.Features.Orders;
using Shop.Api.Features.Products;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Shop")
    ?? throw new InvalidOperationException("ConnectionStrings:Shop is required.");
builder.Services.AddDbContext<ShopDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(160)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PersistenceExceptionHandler>();
builder.Services.AddScoped<OrderService>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
// Binding failures are always 400 responses, never exceptions, in every environment.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddShopAuthentication();
var app = builder.Build();
await DatabaseStartup.InitializeAsync(app.Services);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", DatabaseHealth.CheckAsync);
app.MapAuth();
app.MapProducts();
app.MapOrders();
await app.RunAsync();
public partial class Program;
