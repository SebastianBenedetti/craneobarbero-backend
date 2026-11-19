using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CraneoBarbero.Api.Tests.Auth;

public sealed class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/test/me");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Public_endpoints_do_not_require_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/test/public");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_of_active_user_is_accepted()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        var client = factory.ClientWithToken(factory.TokenFor(user));

        var body = await client.GetFromJsonAsync<JsonElement>("/test/me");

        Assert.Equal(user.Id, body.GetProperty("userId").GetInt32());
        Assert.Equal("client", body.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Deactivated_user_cannot_use_a_valid_token()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        var client = factory.ClientWithToken(factory.TokenFor(user));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/me")).StatusCode);

        await factory.UpdateUserAsync(user.Id, u => u.IsActive = false);

        var response = await client.GetAsync("/test/me");
        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "account_inactive");
    }

    [Fact]
    public async Task Token_of_missing_user_is_rejected()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        user.Id = 999_999;
        var client = factory.ClientWithToken(factory.TokenFor(user));

        var response = await client.GetAsync("/test/me");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Role_comes_from_the_database_not_from_the_token()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        user.Role = UserRole.Admin;
        var forgedRoleToken = factory.TokenFor(user);
        var client = factory.ClientWithToken(forgedRoleToken);

        var response = await client.GetAsync("/test/admin");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Role_change_applies_to_existing_tokens()
    {
        var user = await factory.AddUserAsync(UserRole.Admin);
        var client = factory.ClientWithToken(factory.TokenFor(user));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test/admin")).StatusCode);

        await factory.UpdateUserAsync(user.Id, u => u.Role = UserRole.Barber);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test/admin")).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected_with_specific_code()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        var token = CreateToken(user.Id, ApiFactory.TestSecret, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));

        var response = await factory.ClientWithToken(token).GetAsync("/test/me");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "token_expired");
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var user = await factory.AddUserAsync(UserRole.Admin);
        var token = CreateToken(user.Id, "another-secret-that-is-long-enough-to-sign-tokens", DateTime.UtcNow, DateTime.UtcNow.AddHours(1));

        var response = await factory.ClientWithToken(token).GetAsync("/test/me");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Unsigned_token_is_rejected()
    {
        var user = await factory.AddUserAsync(UserRole.Admin);
        var descriptor = BaseDescriptor(user.Id, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        var response = await factory.ClientWithToken(token).GetAsync("/test/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string CreateToken(int userId, string secret, DateTime notBefore, DateTime expires)
    {
        var descriptor = BaseDescriptor(userId, notBefore, expires);
        descriptor.SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static SecurityTokenDescriptor BaseDescriptor(int userId, DateTime notBefore, DateTime expires) => new()
    {
        Issuer = "craneobarbero-api",
        Audience = "craneobarbero-frontend",
        IssuedAt = notBefore,
        NotBefore = notBefore,
        Expires = expires,
        Subject = new ClaimsIdentity([new Claim(AuthClaims.UserId, userId.ToString()), new Claim(AuthClaims.Role, Roles.Admin)]),
    };
}
