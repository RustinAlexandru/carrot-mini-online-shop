using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Api.Domain;

namespace Shop.Api.Features.Auth;

public static class AuthSetup
{
    public static IServiceCollection AddShopAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>().BindConfiguration(JwtOptions.SectionName).ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<JwtTokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        // Bearer validation reads the same options instance that issuance uses.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = jwt.CreateSigningKey(),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    NameClaimType = JwtRegisteredClaimNames.Sub
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!Guid.TryParse(context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out _))
                            context.Fail("Token has no usable subject.");
                        return Task.CompletedTask;
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }
}
