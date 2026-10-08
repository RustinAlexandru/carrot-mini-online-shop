using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Shop.Api.Common;
using Shop.Api.Data;
using Shop.Api.Domain;

namespace Shop.Api.Features.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app) => app.MapPost("/auth/login", LoginAsync).AllowAnonymous();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static async Task<IResult> LoginAsync(
        LoginRequest request, ShopDbContext db, IPasswordHasher<User> hasher, JwtTokenService tokens, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Email)) errors["email"] = ["Email is required."];
        if (string.IsNullOrEmpty(request.Password)) errors["password"] = ["Password is required."];
        if (errors.Count > 0) return ApiProblems.Validation(errors);

        var email = NormalizeEmail(request.Email!);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);
        // One response for unknown email and wrong password so neither field is disclosed.
        if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password!) == PasswordVerificationResult.Failed)
            return ApiProblems.Unauthorized("Invalid email or password.");

        var issued = tokens.Issue(user);
        return Results.Ok(new LoginResponse(issued.Token, issued.ExpiresAt));
    }
}
