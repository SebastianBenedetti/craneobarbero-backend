using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Calendar;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Payments;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Notifications;

public sealed class AppointmentNotificationTests(PaymentApiFactory factory) : IClassFixture<PaymentApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Manual_appointment_with_a_registered_client_sends_the_confirmation_with_the_agreed_content()
    {
        var s = await SetupAsync();

        var id = await ManualAsync(s, Local(2026, 11, 24, 10, 0), s.ClientUser.Id);

        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email);
        Assert.Equal("Turno confirmado - Cráneo Barbero", mail.Subject);
        Assert.Contains($"{s.ClientUser.Name}, confirmaste tu turno en Cráneo Barbero", mail.TextBody);
        Assert.Contains("Fecha y hora: martes 24 de noviembre de 2026, 10:00 h", mail.TextBody);
        Assert.Contains("Dirección: Calle 1 (Sucursal Agenda)", mail.TextBody);
        Assert.Contains("Número de contacto: 341 555-0000", mail.TextBody);
        Assert.Contains("Servicio: Corte y barba", mail.TextBody);
        Assert.DoesNotContain("Pago:", mail.TextBody);
        Assert.DoesNotContain("1500", mail.TextBody);
        Assert.Contains("https://calendar.google.com/calendar/render?action=TEMPLATE", mail.TextBody);

        Assert.Null(mail.Attachments);
        var ics = await IcsFromApiAsync(s, id);
        Assert.Contains("METHOD:PUBLISH", ics);
        Assert.Contains($"UID:{AppointmentCalendar.Uid(id)}", ics);
        Assert.Contains("DTSTART;TZID=America/Argentina/Buenos_Aires:20261124T100000", ics);
        Assert.Contains("LOCATION:Calle 1", ics);
        Assert.DoesNotContain("1500", ics);

        var notification = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .SingleAsync(n => n.AppointmentId == id && n.Type == NotificationType.Confirmation));
        Assert.Equal(s.ClientUser.Id, notification.UserId);
        await WaitUntilAsync(async () => (await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .SingleAsync(n => n.Id == notification.Id))).Status == NotificationStatus.Sent);
    }

    [Fact]
    public async Task Manual_appointment_without_a_verified_registered_client_sends_nothing()
    {
        var s = await SetupAsync();
        var unverified = await factory.AddUserAsync(UserRole.Client);

        var byName = await ManualAsync(s, Local(2026, 11, 24, 11, 0), null);
        var withUnverified = await ManualAsync(s, Local(2026, 11, 24, 11, 30), unverified.Id);

        var notifications = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.AppointmentId == byName || n.AppointmentId == withUnverified).CountAsync());
        Assert.Equal(0, notifications);
        Assert.Empty(factory.Emails.SentTo(unverified.Email));
    }

    [Fact]
    public async Task Online_confirmation_is_sent_when_the_payment_is_approved_and_includes_the_payment()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 12, 0));
        Assert.Empty(factory.Emails.SentTo(s.ClientUser.Email));

        factory.Gateway.Payments["n-1"] = new GatewayPayment("n-1", "approved", $"appointment:{id}", 1500m, Now);
        Assert.Equal(HttpStatusCode.OK, (await Webhook("n-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Webhook("n-1")).StatusCode);

        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email);
        Assert.Contains("Pago: $1.500 acreditado con Mercado Pago.", mail.TextBody);
        await Task.Delay(100);
        Assert.Single(factory.Emails.SentTo(s.ClientUser.Email));
    }

    [Fact]
    public async Task Cancelling_a_paid_appointment_sends_the_cancellation_with_the_refund_and_a_cancel_calendar()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 15, 0));
        factory.Gateway.Payments["n-2"] = new GatewayPayment("n-2", "approved", $"appointment:{id}", 1500m, Now);
        await Webhook("n-2");
        await factory.Emails.WaitForAsync(s.ClientUser.Email);

        var response = await s.Client.PostAsync($"/appointments/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email, 2);
        Assert.Equal("Turno cancelado - Cráneo Barbero", mail.Subject);
        Assert.Contains($"{s.ClientUser.Name}, cancelaste tu turno en Cráneo Barbero", mail.TextBody);
        Assert.Contains("Te devolvemos $1.500", mail.TextBody);
        Assert.Null(mail.Attachments);
        var ics = await IcsFromApiAsync(s, id);
        Assert.Contains("METHOD:CANCEL", ics);
        Assert.Contains("STATUS:CANCELLED", ics);
        Assert.Contains($"UID:{AppointmentCalendar.Uid(id)}", ics);
    }

    [Fact]
    public async Task Paying_in_the_shop_confirms_at_once_and_cancelling_leaves_it_unpaid()
    {
        var s = await SetupAsync();
        var response = await s.Client.PostAsJsonAsync("/appointments", new
        {
            barberId = s.Barber.Id, cutId = s.Cut.Id, appointmentDate = Local(2026, 11, 24, 17, 30), paymentMethod = "cash",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("checkoutUrl").ValueKind);
        var appointment = body.GetProperty("appointment");
        Assert.Equal("pending", appointment.GetProperty("status").GetString());
        Assert.Equal("unpaid", appointment.GetProperty("paymentStatus").GetString());
        Assert.Equal("cash", appointment.GetProperty("paymentMethod").GetString());
        Assert.Equal(JsonValueKind.Null, appointment.GetProperty("paymentExpiresAt").ValueKind);

        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email);
        Assert.Contains("Pago: $1.500 en efectivo en el local, el día del turno.", mail.TextBody);

        var id = appointment.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/appointments/{id}/cancel", null)).StatusCode);
        var cancelled = await factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(AppointmentStatus.Cancelled, cancelled.Status);
        Assert.Equal(PaymentStatus.Unpaid, cancelled.PaymentStatus);

        var card = await s.Client.PostAsJsonAsync("/appointments", new
        {
            barberId = s.Barber.Id, cutId = s.Cut.Id, appointmentDate = Local(2026, 11, 24, 18, 0), paymentMethod = "card",
        });
        await HttpAssertions.AssertErrorAsync(card, HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Cancelling_an_unpaid_online_reservation_sends_nothing()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 15, 30));

        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/appointments/{id}/cancel", null)).StatusCode);

        await Task.Delay(100);
        Assert.Empty(factory.Emails.SentTo(s.ClientUser.Email));
    }

    [Fact]
    public async Task Rescheduling_sends_the_modification_with_the_new_time()
    {
        var s = await SetupAsync();
        var id = await ManualAsync(s, Local(2026, 11, 24, 16, 0), s.ClientUser.Id);
        await factory.Emails.WaitForAsync(s.ClientUser.Email);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PutAsJsonAsync($"/appointments/{id}/reschedule", new { appointmentDate = Local(2026, 11, 24, 17, 0) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email, 2);
        Assert.Equal("Tu turno cambió - Cráneo Barbero", mail.Subject);
        Assert.Contains("Nueva fecha y hora: martes 24 de noviembre de 2026, 17:00 h", mail.TextBody);
        Assert.Contains("DTSTART;TZID=America/Argentina/Buenos_Aires:20261124T170000", await IcsFromApiAsync(s, id));
    }

    [Fact]
    public async Task Removing_slots_and_cancelling_sends_the_reason_to_the_client()
    {
        var s = await SetupAsync();
        await ManualAsync(s, Local(2026, 11, 24, 18, 0), s.ClientUser.Id);
        await factory.Emails.WaitForAsync(s.ClientUser.Email);
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var body = new
        {
            branchIds = new[] { s.Barber.BranchId },
            barberIds = new[] { s.Barber.Id },
            dateFrom = "2026-11-24",
            dateTo = "2026-11-24",
            timeFrom = "18:00",
            timeTo = "18:30",
            reason = "Corte de luz en el local",
            cancelAffected = true,
            expectedCancellations = 1,
        };

        var response = await admin.PostAsJsonAsync("/schedule/slots/remove", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email, 2);
        Assert.Contains("tu turno en Cráneo Barbero fue cancelado", mail.TextBody);
        Assert.Contains("La barbería no va a atender en ese horario: Corte de luz en el local.", mail.TextBody);
    }

    [Fact]
    public async Task Reminder_goes_out_once_24_hours_before_and_only_for_confirmed_appointments()
    {
        var s = await SetupAsync();
        var start = Local(2026, 11, 25, 10, 0);
        var confirmed = await AddAppointmentAsync(s, start, AppointmentStatus.Pending);
        var cancelled = await AddAppointmentAsync(s, start.AddHours(1), AppointmentStatus.Cancelled);

        try
        {
            factory.Time.SetUtcNow(start.AddHours(-25));
            await RunRemindersAsync();
            Assert.Equal(0, await RemindersForAsync(confirmed));

            factory.Time.SetUtcNow(start.AddHours(-23));
            await RunRemindersAsync();
            await RunRemindersAsync();
            Assert.Equal(1, await RemindersForAsync(confirmed));
            Assert.Equal(0, await RemindersForAsync(cancelled));
        }
        finally
        {
            factory.Time.SetUtcNow(Now);
        }

        var mail = await factory.Emails.WaitForAsync(s.ClientUser.Email);
        Assert.Equal("Recordatorio de tu turno - Cráneo Barbero", mail.Subject);
        Assert.Contains("Fecha y hora: miércoles 25 de noviembre de 2026, 10:00 h", mail.TextBody);
        Assert.Contains("Google Calendar: https://calendar.google.com/calendar/render?action=TEMPLATE", mail.TextBody);
        var reminders = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .Where(n => n.Type == NotificationType.Reminder && (n.AppointmentId == confirmed || n.AppointmentId == cancelled)).ToListAsync());
        var reminder = Assert.Single(reminders);
        Assert.Equal(confirmed, reminder.AppointmentId);
        Assert.Equal(start.AddHours(-24), reminder.ScheduledAt);
    }

    [Fact]
    public async Task Appointments_booked_within_the_last_24_hours_get_no_reminder()
    {
        var s = await SetupAsync();
        var start = Local(2026, 11, 26, 10, 0);
        var id = await AddAppointmentAsync(s, start, AppointmentStatus.Pending);
        await factory.WithDbAsync(async db =>
        {
            (await db.Appointments.SingleAsync(a => a.Id == id)).CreatedAt = start.AddHours(-3);
            return await db.SaveChangesAsync();
        });

        try
        {
            factory.Time.SetUtcNow(start.AddHours(-2));
            await RunRemindersAsync();
        }
        finally
        {
            factory.Time.SetUtcNow(Now);
        }

        var sent = await factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .AnyAsync(n => n.AppointmentId == id && n.Type == NotificationType.Reminder));
        Assert.False(sent);
    }

    [Fact]
    public async Task Owner_downloads_the_calendar_file_without_the_price()
    {
        var s = await SetupAsync();
        var id = await ManualAsync(s, Local(2026, 11, 24, 9, 0), s.ClientUser.Id);
        var other = await factory.ClientForAsync(UserRole.Client);

        var response = await s.Client.GetAsync($"/appointments/{id}/calendar.ics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        var ics = Unfold(await response.Content.ReadAsStringAsync());
        Assert.Contains("SUMMARY:Turno en Cráneo Barbero", ics);
        Assert.Contains("Barbero: Barbero Agenda", ics);
        Assert.Contains("Servicio: Corte y barba", ics);
        Assert.Contains("Sucursal: Sucursal Agenda", ics);
        Assert.Contains("BEGIN:VALARM", ics);
        Assert.DoesNotContain("1500", ics);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/appointments/{id}/calendar.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/appointments/{id}/calendar.ics")).StatusCode);
    }

    [Fact]
    public async Task Unpaid_reservation_has_no_calendar_file()
    {
        var s = await SetupAsync();
        var id = await BookAsync(s, Local(2026, 11, 24, 19, 0));

        await HttpAssertions.AssertErrorAsync(await s.Client.GetAsync($"/appointments/{id}/calendar.ics"), HttpStatusCode.Conflict, "calendar_unavailable");
    }

    [Fact]
    public async Task Signed_link_works_without_a_session_until_it_expires_and_cannot_be_forged()
    {
        var s = await SetupAsync();
        var id = await ManualAsync(s, Local(2026, 11, 24, 19, 30), s.ClientUser.Id);
        var links = factory.Services.GetRequiredService<CalendarLinks>();
        var anonymous = factory.CreateClient();

        var valid = links.CreateToken(id, Now.AddDays(2));
        var expired = links.CreateToken(id, Now.AddMinutes(-1));
        var forged = valid[..^1] + (valid[^1] == '0' ? '1' : '0');
        var otherId = links.CreateToken(id, Now.AddDays(2)).Replace($"{id}.", $"{id + 1}.", StringComparison.Ordinal);

        var ok = await anonymous.GetAsync($"/calendar/{valid}.ics");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("inline", ok.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains($"UID:{AppointmentCalendar.Uid(id)}", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/calendar/{expired}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/calendar/{forged}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/calendar/{otherId}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/calendar/abc.ics")).StatusCode);
    }

    private sealed record Setup(User Barber, Cut Cut, User ClientUser, HttpClient Client);

    private async Task<Setup> SetupAsync()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        await factory.WithDbAsync(async db =>
        {
            (await db.Branches.SingleAsync(b => b.Id == branch.Id)).Phone = "341 555-0000";
            return await db.SaveChangesAsync();
        });
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday.AddDays(2));
        var cut = await factory.WithDbAsync(async db =>
        {
            var c = new Cut { Name = "Corte y barba", Price = 1500m, DurationMinutes = 30 };
            db.Cuts.Add(c);
            await db.SaveChangesAsync();
            return c;
        });
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);
        return new Setup(barber, cut, user, factory.ClientWithToken(factory.TokenFor(user)));
    }

    private async Task<int> ManualAsync(Setup s, DateTimeOffset start, int? clientId)
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var response = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = s.Barber.Id,
            cutId = s.Cut.Id,
            appointmentDate = start,
            clientId,
            clientName = clientId is null ? "Cliente de mostrador" : null,
            paymentMethod = "cash",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int> BookAsync(Setup s, DateTimeOffset start)
    {
        var response = await s.Client.PostAsJsonAsync("/appointments", new { barberId = s.Barber.Id, cutId = s.Cut.Id, appointmentDate = start });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appointment").GetProperty("id").GetInt32();
    }

    private Task<int> AddAppointmentAsync(Setup s, DateTimeOffset start, AppointmentStatus status) =>
        factory.WithDbAsync(async db =>
        {
            var appointment = new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(30),
                Status = status,
                PaymentStatus = PaymentStatus.Unpaid,
                PaymentMethod = PaymentMethod.Cash,
                ClientId = s.ClientUser.Id,
                BarberId = s.Barber.Id,
                CutId = s.Cut.Id,
                BranchId = s.Barber.BranchId!.Value,
                Price = s.Cut.Price,
                CreatedByRole = UserRole.Admin,
                CreatedAt = start.AddDays(-5),
            };
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            return appointment.Id;
        });

    private Task<int> RemindersForAsync(int appointmentId) =>
        factory.WithDbAsync(db => db.Notifications.AsNoTracking()
            .CountAsync(n => n.AppointmentId == appointmentId && n.Type == NotificationType.Reminder));

    private async Task<ReminderRunResult> RunRemindersAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppointmentReminders>().RunAsync();
    }

    private Task<HttpResponseMessage> Webhook(string dataId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/payments/webhook?type=payment&data.id={dataId}")
        {
            Content = JsonContent.Create(new { type = "payment", data = new { id = dataId } }),
        };
        request.Headers.Add("x-signature", "valida");
        request.Headers.Add("x-request-id", "req");
        return factory.CreateClient().SendAsync(request);
    }

    private static async Task<string> IcsFromApiAsync(Setup s, int id)
    {
        var response = await s.Client.GetAsync($"/appointments/{id}/calendar.ics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Unfold(await response.Content.ReadAsStringAsync());
    }

    private static string Unfold(string ics) => ics.Replace($"{(char)13}{(char)10} ", string.Empty, StringComparison.Ordinal);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("La condición no se cumplió a tiempo.");
    }
}
