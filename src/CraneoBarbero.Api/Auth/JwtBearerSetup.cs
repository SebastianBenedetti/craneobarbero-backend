using System.Security.Claims;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CraneoBarbero.Api.Auth;

public sealed class JwtBearerSetup(IOptions<JwtSettings> settings) : IConfigureNamedOptions<JwtBearerOptions>
{
    private const string FailureCodeKey = "auth_failure_code";

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        Configure(options);
    }

    public void Configure(JwtBearerOptions options)
    {
        var jwt = settings.Value;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.CreateSigningKey(jwt.Secret),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = AuthClaims.UserId,
            RoleClaimType = AuthClaims.Role,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ValidateUserAsync,
            OnChallenge = WriteChallengeAsync,
        };
    }

    private static async Task ValidateUserAsync(TokenValidatedContext context)
    {
        var userId = context.Principal?.GetUserId();
        if (userId is null)
        {
            context.Fail("Token sin usuario.");
            return;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IUserAccessStore>();
        var access = await store.FindAsync(userId.Value, context.HttpContext.RequestAborted);
        if (access is null)
        {
            context.Fail("Usuario inexistente.");
            return;
        }

        if (!access.IsActive)
        {
            context.HttpContext.Items[FailureCodeKey] = ErrorCodes.AccountInactive;
            context.Fail("Usuario desactivado.");
            return;
        }

        var identity = new ClaimsIdentity(
            context.Principal!.Claims.Where(claim => claim.Type != AuthClaims.Role),
            JwtBearerDefaults.AuthenticationScheme,
            AuthClaims.UserId,
            AuthClaims.Role);
        identity.AddClaim(new Claim(AuthClaims.Role, Roles.ToClaimValue(access.Role)));
        context.Principal = new ClaimsPrincipal(identity);
    }

    private static Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";

        var body = context.HttpContext.Items[FailureCodeKey] as string == ErrorCodes.AccountInactive
            ? new ApiErrorResponse(ErrorMessages.AccountInactive, ErrorCodes.AccountInactive)
            : context.AuthenticateFailure is SecurityTokenExpiredException
                ? new ApiErrorResponse(ErrorMessages.TokenExpired, ErrorCodes.TokenExpired)
                : new ApiErrorResponse(ErrorMessages.Unauthorized, ErrorCodes.Unauthorized);

        return ErrorResponseWriter.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, body);
    }
}
