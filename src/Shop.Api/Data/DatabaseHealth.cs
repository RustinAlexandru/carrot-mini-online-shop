using Microsoft.EntityFrameworkCore;

namespace Shop.Api.Data;

public static class DatabaseHealth
{
    public static async Task<IResult> CheckAsync(ShopDbContext db, CancellationToken cancellationToken)
        => await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { status = "healthy" })
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}
