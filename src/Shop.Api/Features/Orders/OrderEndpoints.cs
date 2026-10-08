using System.Security.Claims;
using Shop.Api.Common;
using Shop.Api.Features.Auth;

namespace Shop.Api.Features.Orders;

public static class OrderEndpoints
{
    public static void MapOrders(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders").RequireAuthorization();
        orders.MapPost("/", async (CreateOrderRequest request, ClaimsPrincipal user, OrderService service, CancellationToken ct)
            => ApiProblems.ToResult(await service.CreateAsync(user.GetUserId(), request, ct),
                order => Results.Created($"/orders/{order.Id}", order)));
        orders.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, OrderService service, CancellationToken ct)
            => ApiProblems.ToResult(await service.GetAsync(user.GetUserId(), id, ct), Results.Ok));
        orders.MapPut("/{id:guid}", async (Guid id, UpdateOrderRequest request, ClaimsPrincipal user, OrderService service, CancellationToken ct)
            => ApiProblems.ToResult(await service.ReplaceAsync(user.GetUserId(), id, request, ct), Results.Ok));
        orders.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, OrderService service, CancellationToken ct)
            => await service.DeleteAsync(user.GetUserId(), id, ct) is { } error ? ApiProblems.ToResult(error) : Results.NoContent());
    }
}
