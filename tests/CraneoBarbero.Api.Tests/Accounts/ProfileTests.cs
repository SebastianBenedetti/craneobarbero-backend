using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Accounts;

public sealed class ProfileTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Clave2026segura";

    [Fact]
    public async Task Me_requires_a_session()
    {
        var response = await factory.CreateClient().GetAsync("/me");

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData(UserRole.Client)]
    [InlineData(UserRole.Barber)]
    [InlineData(UserRole.Admin)]
    public async Task Me_returns_the_own_profile_without_the_password(UserRole role)
    {
        var user = await factory.AddUserAsync(role);

        var response = await factory.ClientWithToken(factory.TokenFor(user)).GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user.Id, body.GetProperty("id").GetInt32());
        Assert.Equal(user.Email, body.GetProperty("email").GetString());
        Assert.False(body.TryGetProperty("password", out _));
        Assert.False(body.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task Update_profile_only_changes_the_allowed_fields()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        var client = factory.ClientWithToken(factory.TokenFor(user));

        var response = await client.PutAsJsonAsync("/me", new
        {
            name = "  Nombre Nuevo ",
            phone = "+54 341 555-0000",
            imageUrl = "https://example.com/foto.jpg",
            email = "otro@example.com",
            role = "admin",
            isActive = false,
            emailVerified = true,
            branchId = 1,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.Equal("Nombre Nuevo", stored.Name);
        Assert.Equal("+54 341 555-0000", stored.Phone);
        Assert.Equal("https://example.com/foto.jpg", stored.ImageUrl);
        Assert.Equal(user.Email, stored.Email);
        Assert.Equal(UserRole.Client, stored.Role);
        Assert.True(stored.IsActive);
        Assert.False(stored.EmailVerified);
        Assert.Null(stored.BranchId);
    }

    [Fact]
    public async Task Update_profile_validates_the_fields()
    {
        var client = await factory.ClientForAsync(UserRole.Client);

        var response = await client.PutAsJsonAsync("/me", new { name = "A", phone = "llamame", imageUrl = "javascript:alert(1)" });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        var errors = json.GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("phone", out _));
        Assert.True(errors.TryGetProperty("imageUrl", out _));
    }

    [Theory]
    [InlineData("/images/seba.jpg", HttpStatusCode.OK)]
    [InlineData("https://example.com/foto.jpg", HttpStatusCode.OK)]
    [InlineData("//evil.example.com/foto.jpg", HttpStatusCode.BadRequest)]
    [InlineData("/\\evil.example.com/foto.jpg", HttpStatusCode.BadRequest)]
    [InlineData("images/foto.jpg", HttpStatusCode.BadRequest)]
    [InlineData("/images/mi foto.jpg", HttpStatusCode.BadRequest)]
    public async Task Image_accepts_web_addresses_and_site_paths(string imageUrl, HttpStatusCode expected)
    {
        var client = await factory.ClientForAsync(UserRole.Client);

        var response = await client.PutAsJsonAsync("/me", new { name = "Nombre Nuevo", imageUrl });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_the_current_password()
    {
        var (email, client) = await RegisterAsync();

        var wrong = await client.PutAsJsonAsync("/me/password",
            new { currentPassword = "NoEsLaClave1", newPassword = "NuevaClave2026" });
        await HttpAssertions.AssertErrorAsync(wrong, HttpStatusCode.BadRequest, "invalid_current_password");

        var same = await client.PutAsJsonAsync("/me/password",
            new { currentPassword = Password, newPassword = Password });
        await HttpAssertions.AssertErrorAsync(same, HttpStatusCode.BadRequest, "same_password");

        var weak = await client.PutAsJsonAsync("/me/password",
            new { currentPassword = Password, newPassword = "debil" });
        await HttpAssertions.AssertErrorAsync(weak, HttpStatusCode.BadRequest, "validation_error");

        var ok = await client.PutAsJsonAsync("/me/password",
            new { currentPassword = Password, newPassword = "NuevaClave2026" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "NuevaClave2026" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/auth/login", new { email, password = Password })).StatusCode);
    }

    private async Task<(string Email, HttpClient Client)> RegisterAsync()
    {
        var email = $"perfil-{Guid.NewGuid():N}@example.com";
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Cliente", email, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (email, factory.ClientWithToken(body.GetProperty("token").GetString()!));
    }
}
