using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Shop.Api.Features.Auth;

public static class UserClaims
{
    /// <summary>The authenticated subject; bearer validation already rejected tokens without a Guid sub.</summary>
    public static Guid GetUserId(this ClaimsPrincipal user)
        => Guid.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? throw new InvalidOperationException("Authenticated user has no subject."));
}
