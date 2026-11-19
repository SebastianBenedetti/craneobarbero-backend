using System.Globalization;
using System.Security.Claims;
using System.Text;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CraneoBarbero.Api.Auth;

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

public interface IJwtTokenService
{
    IssuedToken CreateToken(User user);
}

public sealed class JwtTokenService(IOptions<JwtSettings> settings, TimeProvider timeProvider) : IJwtTokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken CreateToken(User user)
    {
        var jwt = settings.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.ExpirationMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(AuthClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(AuthClaims.Role, Roles.ToClaimValue(user.Role)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(CreateSigningKey(jwt.Secret), SecurityAlgorithms.HmacSha256),
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expiresAt);
    }

    public static SymmetricSecurityKey CreateSigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));
}
