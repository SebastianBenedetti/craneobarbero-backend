using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Accounts;

public sealed class AccountClosureTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private const string Password = "Clave2026segura";

    [Fact]
    public async Task Preview_lists_the_future_appointments_and_the_refund()
    {
        var s = await SetupAsync();
        await AddAsync(s, Local(2026, 11, 20, 10, 0), a => a.Status = AppointmentStatus.Completed);
        await AddAsync(s, Local(2026, 11, 25, 10, 0), a => a.Status = AppointmentStatus.Cancelled);
        await OnlinePaidAsync(s, Local(2026, 11, 25, 11, 0));
        await OnlinePaidAsync(s, Now.AddHours(2));
        await AddAsync(s, Local(2026, 11, 26, 10, 0));

        var preview = await s.Client.GetFromJsonAsync<JsonElement>("/me/closure-preview");

        Assert.Equal(3, preview.GetProperty("futureAppointments").GetInt32());
        Assert.Equal(1, preview.GetProperty("refundableAppointments").GetInt32());
        Assert.Equal(1000m, preview.GetProperty("totalRefund").GetDecimal());
        Assert.Equal(3, preview.GetProperty("appointments").GetArrayLength());
    }

    [Fact]
    public async Task Deactivate_requires_the_password_and_cancels_the_future_appointments()
    {
        var s = await SetupAsync();
        var early = await OnlinePaidAsync(s, Local(2026, 11, 25, 12, 0));
        var late = await OnlinePaidAsync(s, Now.AddHours(2));
        var past = await AddAsync(s, Local(2026, 11, 20, 10, 0), a => a.Status = AppointmentStatus.Completed);

        var wrong = await s.Client.PostAsJsonAsync("/me/deactivate", new { password = "Otra2026clave" });
        await HttpAssertions.AssertErrorAsync(wrong, HttpStatusCode.BadRequest, "invalid_password");
        Assert.True((await StoredUserAsync(s.ClientUser.Id))!.IsActive);

        var response = await s.Client.PostAsJsonAsync("/me/deactivate", new { password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, json.GetProperty("cancelledAppointments").GetInt32());
        Assert.Equal(1, json.GetProperty("refundedAppointments").GetInt32());
        Assert.Equal(1000m, json.GetProperty("totalRefunded").GetDecimal());
        Assert.False((await StoredUserAsync(s.ClientUser.Id))!.IsActive);
        Assert.Equal(PaymentStatus.Refunded, (await AppointmentAsync(early)).PaymentStatus);
        var lateStored = await AppointmentAsync(late);
        Assert.Equal(AppointmentStatus.Cancelled, lateStored.Status);
        Assert.Equal(PaymentStatus.Paid, lateStored.PaymentStatus);
        Assert.Equal(UserRole.Client, lateStored.CancelledByRole);
        Assert.Equal(AppointmentStatus.Completed, (await AppointmentAsync(past)).Status);
        await factory.Emails.WaitForAsync(s.ClientUser.Email, 2);

        await HttpAssertions.AssertErrorAsync(await s.Client.GetAsync("/me"), HttpStatusCode.Unauthorized, "account_inactive");
        var login = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { email = s.ClientUser.Email, password = Password });
        await HttpAssertions.AssertErrorAsync(login, HttpStatusCode.Forbidden, "account_inactive");
    }

    [Fact]
    public async Task Delete_removes_the_user_and_anonymizes_the_history()
    {
        var s = await SetupAsync();
        var past = await AddAsync(s, Local(2026, 11, 20, 10, 0), a =>
        {
            a.Status = AppointmentStatus.Completed;
            a.PaymentStatus = PaymentStatus.Paid;
        });
        var future = await AddAsync(s, Local(2026, 11, 25, 15, 0));
        await factory.WithDbAsync(async db =>
        {
            db.AuthTokens.Add(new AuthToken
            {
                UserId = s.ClientUser.Id,
                Type = AuthTokenType.PasswordReset,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.AddHours(1),
            });
            return await db.SaveChangesAsync();
        });

        var wrong = await s.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/me")
        {
            Content = JsonContent.Create(new { password = "Otra2026clave" }),
        });
        await HttpAssertions.AssertErrorAsync(wrong, HttpStatusCode.BadRequest, "invalid_password");

        var response = await s.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/me")
        {
            Content = JsonContent.Create(new { password = Password }),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredUserAsync(s.ClientUser.Id));
        var history = await AppointmentAsync(past);
        Assert.Null(history.ClientId);
        Assert.Equal("Cliente eliminado", history.ClientName);
        Assert.Equal(PaymentStatus.Paid, history.PaymentStatus);
        var cancelled = await AppointmentAsync(future);
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.ClientId);
        Assert.Equal("account_deleted", cancelled.CancellationReason);
        Assert.Equal(0, await factory.WithDbAsync(db => db.AuthTokens.CountAsync(t => t.UserId == s.ClientUser.Id)));
        await HttpAssertions.AssertErrorAsync(await s.Client.GetAsync("/me"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Theory]
    [InlineData(UserRole.Barber)]
    [InlineData(UserRole.Admin)]
    public async Task Only_clients_can_close_their_own_account(UserRole role)
    {
        var client = await factory.ClientForAsync(role);

        await HttpAssertions.AssertErrorAsync(await client.GetAsync("/me/closure-preview"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(
            await client.PostAsJsonAsync("/me/deactivate", new { password = Password }), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Admin_deactivating_a_client_cancels_the_future_appointments_and_can_reactivate_it()
    {
        var s = await SetupAsync();
        var future = await OnlinePaidAsync(s, Local(2026, 11, 25, 16, 0));
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var preview = await admin.GetFromJsonAsync<JsonElement>($"/users/{s.ClientUser.Id}/closure-preview");
        Assert.Equal(1, preview.GetProperty("futureAppointments").GetInt32());

        var response = await admin.PostAsync($"/users/{s.ClientUser.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("user").GetProperty("isActive").GetBoolean());
        Assert.Equal(1, json.GetProperty("refundedAppointments").GetInt32());
        var stored = await AppointmentAsync(future);
        Assert.Equal(AppointmentStatus.Cancelled, stored.Status);
        Assert.Equal(UserRole.Admin, stored.CancelledByRole);
        Assert.Equal("account_deactivated", stored.CancellationReason);
        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email);
        Assert.Contains("Tu cuenta fue desactivada por el local.", mail.TextBody);

        var activated = await admin.PostAsync($"/users/{s.ClientUser.Id}/activate", null);
        Assert.True((await activated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Admin_deletes_clients_only()
    {
        var s = await SetupAsync();
        var past = await AddAsync(s, Local(2026, 11, 20, 11, 0), a => a.Status = AppointmentStatus.Completed);
        var otherAdmin = await factory.AddUserAsync(UserRole.Admin);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        await HttpAssertions.AssertErrorAsync(await admin.DeleteAsync($"/users/{otherAdmin.Id}"), HttpStatusCode.BadRequest, "not_a_client");
        await HttpAssertions.AssertErrorAsync(await admin.DeleteAsync($"/users/{s.Barber.Id}"), HttpStatusCode.BadRequest, "not_a_client");

        var response = await admin.DeleteAsync($"/users/{s.ClientUser.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await StoredUserAsync(s.ClientUser.Id));
        Assert.Equal("Cliente eliminado", (await AppointmentAsync(past)).ClientName);
        await HttpAssertions.AssertErrorAsync(await admin.DeleteAsync($"/users/{s.ClientUser.Id}"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_deactivated()
    {
        var admin = await factory.AddUserAsync(UserRole.Admin);
        var other = await factory.AddUserAsync(UserRole.Admin);
        await factory.WithDbAsync(async db =>
        {
            foreach (var user in await db.Users.Where(u => u.Role == UserRole.Admin && u.Id != admin.Id && u.Id != other.Id).ToListAsync())
            {
                user.IsActive = false;
            }

            return await db.SaveChangesAsync();
        });
        var client = factory.ClientWithToken(factory.TokenFor(admin));

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/users/{other.Id}/deactivate", null)).StatusCode);
        await HttpAssertions.AssertErrorAsync(await client.PostAsync($"/users/{admin.Id}/deactivate", null), HttpStatusCode.Conflict, "last_admin");
        Assert.True((await StoredUserAsync(admin.Id))!.IsActive);
    }

    private sealed record Setup(User Barber, Cut Cut, User ClientUser, HttpClient Client);

    private async Task<Setup> SetupAsync()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var client = await factory.AddUserAsync(UserRole.Client);
        var hash = factory.Services.GetRequiredService<IPasswordHasher>().Hash(Password);
        await factory.UpdateUserAsync(client.Id, u =>
        {
            u.EmailVerified = true;
            u.PasswordHash = hash;
        });
        return new Setup(barber, cut, client, factory.ClientWithToken(factory.TokenFor(client)));
    }

    private Task<int> AddAsync(Setup s, DateTimeOffset start, Action<Appointment>? change = null) =>
        factory.WithDbAsync(async db =>
        {
            var appointment = new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(30),
                Status = AppointmentStatus.Pending,
                ClientId = s.ClientUser.Id,
                BarberId = s.Barber.Id,
                CutId = s.Cut.Id,
                BranchId = s.Barber.BranchId!.Value,
                Price = 1000m,
                PaymentStatus = PaymentStatus.Unpaid,
                PaymentMethod = PaymentMethod.MercadoPago,
                CreatedByRole = UserRole.Client,
            };
            change?.Invoke(appointment);
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            return appointment.Id;
        });

    private async Task<int> OnlinePaidAsync(Setup s, DateTimeOffset start)
    {
        var id = await AddAsync(s, start, a =>
        {
            a.PaymentStatus = PaymentStatus.Paid;
            a.PaidAt = Now.AddDays(-1);
        });
        await factory.WithDbAsync(async db =>
        {
            db.Payments.Add(new Payment
            {
                AppointmentId = id,
                Amount = 1000m,
                Method = PaymentMethod.MercadoPago,
                Status = TransactionStatus.Approved,
                PaidAt = Now.AddDays(-1),
            });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private Task<User?> StoredUserAsync(int id) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id));

    private Task<Appointment> AppointmentAsync(int id) =>
        factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
}
