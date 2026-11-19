using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Auth;

public static class Roles
{
    public const string Client = "client";
    public const string Barber = "barber";
    public const string Admin = "admin";

    public static string ToClaimValue(UserRole role) => role switch
    {
        UserRole.Client => Client,
        UserRole.Barber => Barber,
        UserRole.Admin => Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

public static class AuthClaims
{
    public const string UserId = "sub";
    public const string Id = "id";
    public const string Name = "name";
    public const string Role = "role";
}
