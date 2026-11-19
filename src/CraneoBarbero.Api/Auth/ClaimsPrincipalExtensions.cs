using System.Globalization;
using System.Security.Claims;
using CraneoBarbero.Api.Common.Errors;

namespace CraneoBarbero.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static int? GetUserId(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(AuthClaims.UserId);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static int GetRequiredUserId(this ClaimsPrincipal user) =>
        user.GetUserId() ?? throw new UnauthorizedException(ErrorMessages.Unauthorized);

    public static string? GetRole(this ClaimsPrincipal user) => user.FindFirstValue(AuthClaims.Role);
}
