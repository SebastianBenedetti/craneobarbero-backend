using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Accounts;

public sealed class AccountFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Password = "Clave2026segura";

    [Fact]
    public async Task Register_creates_an_unverified_client_and_starts_a_session()
    {
        var email = NewEmail();

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "  Ana Pérez ", email = $"  {email.ToUpperInvariant()} ", password = Password, phone = "341 555-1234", acceptTerms = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        var user = body.GetProperty("user");
        Assert.Equal("client", user.GetProperty("role").GetString());
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("Ana Pérez", user.GetProperty("name").GetString());
        Assert.False(user.GetProperty("emailVerified").GetBoolean());
        Assert.False(user.TryGetProperty("password", out _));
        Assert.False(user.TryGetProperty("passwordHash", out _));

        var stored = await FindUserAsync(email);
        Assert.StartsWith("$2", stored.PasswordHash);
        Assert.DoesNotContain(Password, stored.PasswordHash);
        Assert.True(stored.IsActive);
        Assert.Null(stored.BranchId);
        Assert.NotNull(stored.TermsAcceptedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Register_requires_accepting_the_terms(bool? acceptTerms)
    {
        var email = NewEmail();

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Ana", email, password = Password, acceptTerms });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.GetProperty("errors").TryGetProperty("acceptTerms", out _));
        Assert.Null(await FindUserOrNullAsync(email));
    }

    [Fact]
    public async Task Register_never_creates_staff_accounts()
    {
        var email = NewEmail();

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Intruso", email, password = Password, role = "admin", emailVerified = true, isActive = true, acceptTerms = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await FindUserAsync(email);
        Assert.Equal(UserRole.Client, stored.Role);
        Assert.False(stored.EmailVerified);
    }

    [Fact]
    public async Task Register_sends_the_verification_email_and_records_the_notification()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var message = await factory.Emails.WaitForAsync(email);
        Assert.Equal("Confirmá tu email - Cráneo Barbero", message.Subject);
        Assert.Contains($"{ApiFactory.AllowedOrigin}/verify-email?token=", message.TextBody);
        Assert.Contains("24 horas", message.TextBody);

        var token = FakeEmailSender.ExtractToken(message);
        var user = await FindUserAsync(email);
        var stored = await factory.WithDbAsync(db =>
            db.AuthTokens.SingleAsync(t => t.UserId == user.Id && t.Type == AuthTokenType.EmailVerification));
        Assert.Equal(AuthTokenService.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.InRange(stored.ExpiresAt - stored.CreatedAt, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));

        var notification = await WaitForNotificationAsync(user.Id, NotificationType.EmailVerification);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.NotNull(notification.SentAt);
        Assert.Equal(NotificationChannel.Email, notification.Channel);
    }

    [Fact]
    public async Task Email_html_escapes_user_data()
    {
        var email = NewEmail();
        await RegisterAsync(email, name: "<b>Ana</b>");

        var message = await factory.Emails.WaitForAsync(email);

        Assert.DoesNotContain("<b>Ana</b>", message.HtmlBody);
        Assert.Contains("&lt;b&gt;Ana&lt;/b&gt;", message.HtmlBody);
    }

    [Fact]
    public async Task Register_rejects_an_email_already_in_use_ignoring_case()
    {
        var email = NewEmail();
        await RegisterAsync(email);

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Otra persona", email = email.ToUpperInvariant(), password = Password, acceptTerms = true });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "email_in_use");
    }

    [Theory]
    [InlineData("corta1")]
    [InlineData("sololetrasaqui")]
    [InlineData("1234567890")]
    [InlineData("Una clave muy larga que supera los setenta y dos bytes permitidos por bcrypt 123")]
    public async Task Register_enforces_the_password_policy(string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Ana", email = NewEmail(), password, acceptTerms = true });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.GetProperty("errors").TryGetProperty("password", out _));
    }

    [Theory]
    [InlineData("sin-arroba.com")]
    [InlineData("ana@dominio")]
    [InlineData("")]
    public async Task Register_rejects_invalid_emails(string email)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register",
            new { name = "Ana", email, password = Password, acceptTerms = true });

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Verify_email_marks_the_user_as_verified_once()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var token = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/verify-email", new { token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await FindUserAsync(email)).EmailVerified);

        var reused = await client.PostAsJsonAsync("/auth/verify-email", new { token });
        await HttpAssertions.AssertErrorAsync(reused, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Fact]
    public async Task Verify_email_rejects_expired_and_unknown_tokens()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var token = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email));
        await ExpireTokensAsync(email, AuthTokenType.EmailVerification);
        var client = factory.CreateClient();

        var expired = await client.PostAsJsonAsync("/auth/verify-email", new { token });
        var unknown = await client.PostAsJsonAsync("/auth/verify-email", new { token = "token-inventado" });

        await HttpAssertions.AssertErrorAsync(expired, HttpStatusCode.BadRequest, "invalid_token");
        await HttpAssertions.AssertErrorAsync(unknown, HttpStatusCode.BadRequest, "invalid_token");
        Assert.False((await FindUserAsync(email)).EmailVerified);
    }

    [Fact]
    public async Task Verification_token_cannot_reset_a_password()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var token = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email));

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/reset-password",
            new { token, password = "OtraClave2026" });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Fact]
    public async Task Resend_verification_requires_a_session()
    {
        var response = await factory.CreateClient().PostAsync("/auth/resend-verification", null);

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Resend_verification_waits_a_minute_and_replaces_the_previous_link()
    {
        var email = NewEmail();
        var session = await RegisterAsync(email);
        var firstToken = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email));
        var client = factory.ClientWithToken(session);

        var tooSoon = await client.PostAsync("/auth/resend-verification", null);
        await HttpAssertions.AssertErrorAsync(tooSoon, HttpStatusCode.TooManyRequests, "too_many_requests");

        await AgeTokensAsync(email, AuthTokenType.EmailVerification, TimeSpan.FromMinutes(2));
        var resent = await client.PostAsync("/auth/resend-verification", null);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);

        var secondToken = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email, count: 2));
        Assert.NotEqual(firstToken, secondToken);

        var old = await factory.CreateClient().PostAsJsonAsync("/auth/verify-email", new { token = firstToken });
        await HttpAssertions.AssertErrorAsync(old, HttpStatusCode.BadRequest, "invalid_token");
        var current = await factory.CreateClient().PostAsJsonAsync("/auth/verify-email", new { token = secondToken });
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task Resend_verification_is_rejected_when_already_verified()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);

        var response = await factory.ClientWithToken(factory.TokenFor(user)).PostAsync("/auth/resend-verification", null);

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "email_already_verified");
        Assert.Empty(factory.Emails.SentTo(user.Email));
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_whether_or_not_the_email_exists()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var unknown = NewEmail();
        var client = factory.CreateClient();

        var existing = await client.PostAsJsonAsync("/auth/forgot-password", new { email });
        var missing = await client.PostAsJsonAsync("/auth/forgot-password", new { email = unknown });

        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(existing.StatusCode, missing.StatusCode);
        Assert.Equal(await existing.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());

        var message = await factory.Emails.WaitForAsync(email, count: 2);
        Assert.Equal("Restablecé tu contraseña - Cráneo Barbero", message.Subject);
        Assert.Contains($"{ApiFactory.AllowedOrigin}/reset-password?token=", message.TextBody);
        Assert.Empty(factory.Emails.SentTo(unknown));
    }

    [Fact]
    public async Task Forgot_password_does_not_email_deactivated_accounts()
    {
        var user = await factory.AddUserAsync(UserRole.Client, isActive: false);

        var response = await factory.CreateClient().PostAsJsonAsync("/auth/forgot-password", new { email = user.Email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(200);
        Assert.Empty(factory.Emails.SentTo(user.Email));
    }

    [Fact]
    public async Task Forgot_password_sends_at_most_one_email_per_minute()
    {
        var user = await factory.AddUserAsync(UserRole.Barber);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/forgot-password", new { email = user.Email })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/forgot-password", new { email = user.Email })).StatusCode);

        await factory.Emails.WaitForAsync(user.Email);
        await Task.Delay(200);
        Assert.Single(factory.Emails.SentTo(user.Email));
    }

    [Fact]
    public async Task Reset_password_changes_the_password_once()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/forgot-password", new { email });
        var token = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email, count: 2));

        var response = await client.PostAsJsonAsync("/auth/reset-password", new { token, password = "NuevaClave2026" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, "NuevaClave2026")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(email, Password)).StatusCode);
        Assert.True((await FindUserAsync(email)).EmailVerified);

        var reused = await client.PostAsJsonAsync("/auth/reset-password", new { token, password = "OtraClave2027" });
        await HttpAssertions.AssertErrorAsync(reused, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Fact]
    public async Task Reset_password_rejects_expired_tokens_and_weak_passwords()
    {
        var email = NewEmail();
        await RegisterAsync(email);
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/auth/forgot-password", new { email });
        var token = FakeEmailSender.ExtractToken(await factory.Emails.WaitForAsync(email, count: 2));

        var weak = await client.PostAsJsonAsync("/auth/reset-password", new { token, password = "123" });
        await HttpAssertions.AssertErrorAsync(weak, HttpStatusCode.BadRequest, "validation_error");

        await ExpireTokensAsync(email, AuthTokenType.PasswordReset);
        var expired = await client.PostAsJsonAsync("/auth/reset-password", new { token, password = "NuevaClave2026" });
        await HttpAssertions.AssertErrorAsync(expired, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(email, Password)).StatusCode);
    }

    [Fact]
    public async Task Failed_sends_are_recorded_as_failed()
    {
        var email = NewEmail();
        factory.Emails.Fail = true;
        try
        {
            await RegisterAsync(email);
            var user = await FindUserAsync(email);

            var notification = await WaitForNotificationAsync(user.Id, NotificationType.EmailVerification);

            Assert.Equal(NotificationStatus.Failed, notification.Status);
            Assert.Null(notification.SentAt);
        }
        finally
        {
            factory.Emails.Fail = false;
        }
    }

    private async Task<string> RegisterAsync(string email, string name = "Cliente de prueba")
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/register", new { name, email, password = Password, acceptTerms = true });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        factory.CreateClient().PostAsJsonAsync("/auth/login", new { email, password });

    private Task<User> FindUserAsync(string email) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));

    private Task<User?> FindUserOrNullAsync(string email) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == email));

    private async Task ExpireTokensAsync(string email, AuthTokenType type)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            foreach (var token in await db.AuthTokens.Where(t => t.UserId == user.Id && t.Type == type).ToListAsync())
            {
                token.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }

            return await db.SaveChangesAsync();
        });
    }

    private async Task AgeTokensAsync(string email, AuthTokenType type, TimeSpan age)
    {
        await factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == email);
            foreach (var token in await db.AuthTokens.Where(t => t.UserId == user.Id && t.Type == type).ToListAsync())
            {
                token.CreatedAt -= age;
            }

            return await db.SaveChangesAsync();
        });
    }

    private async Task<Notification> WaitForNotificationAsync(int userId, NotificationType type)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var notification = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
                .SingleAsync(n => n.UserId == userId && n.Type == type));
            if (notification.Status != NotificationStatus.Pending || DateTime.UtcNow > deadline)
            {
                return notification;
            }

            await Task.Delay(20);
        }
    }

    private static string NewEmail() => $"cliente-{Guid.NewGuid():N}@example.com";
}
