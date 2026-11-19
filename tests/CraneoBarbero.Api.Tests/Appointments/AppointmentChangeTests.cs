using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Appointments;

public sealed class AppointmentChangeTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Client_cancelling_a_paid_online_appointment_with_time_gets_a_full_refund()
    {
        var s = await SetupAsync();
        var id = await OnlinePaidAsync(s, Local(2026, 11, 24, 10, 0));

        var preview = await s.Client.GetFromJsonAsync<JsonElement>($"/appointments/{id}/cancellation-preview");
        Assert.True(preview.GetProperty("withinWindow").GetBoolean());
        Assert.True(preview.GetProperty("refundable").GetBoolean());
        Assert.Equal(1000m, preview.GetProperty("amount").GetDecimal());

        var response = await s.Client.PostAsJsonAsync($"/appointments/{id}/cancel", new { reason = "  No llego " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", json.GetProperty("status").GetString());
        Assert.Equal("refunded", json.GetProperty("paymentStatus").GetString());
        Assert.Equal("client", json.GetProperty("cancelledByRole").GetString());
        Assert.Equal("No llego", json.GetProperty("cancellationReason").GetString());
        var stored = await factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
        Assert.Equal(s.ClientUser.Id, stored.CancelledById);
        Assert.Equal(Now, stored.CancelledAt);
        var payment = await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.AppointmentId == id));
        Assert.Equal(TransactionStatus.Refunded, payment.Status);
        Assert.Contains("10:00", await TimesAsync(s.Barber.Id, s.Cut.Id));
    }

    [Fact]
    public async Task Client_cancelling_with_less_than_four_hours_keeps_the_payment()
    {
        var s = await SetupAsync();
        var id = await OnlinePaidAsync(s, Now.AddHours(2));

        var preview = await s.Client.GetFromJsonAsync<JsonElement>($"/appointments/{id}/cancellation-preview");
        Assert.False(preview.GetProperty("withinWindow").GetBoolean());
        Assert.False(preview.GetProperty("refundable").GetBoolean());
        Assert.Equal(1000m, preview.GetProperty("keptAmount").GetDecimal());
        Assert.Equal(2.0, preview.GetProperty("hoursLeft").GetDouble());
        Assert.Contains("no se realizará el reembolso", preview.GetProperty("message").GetString());

        var json = await (await s.Client.PostAsync($"/appointments/{id}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("cancelled", json.GetProperty("status").GetString());
        Assert.Equal("paid", json.GetProperty("paymentStatus").GetString());
        var payment = await factory.WithDbAsync(db => db.Payments.AsNoTracking().SingleAsync(p => p.AppointmentId == id));
        Assert.Equal(TransactionStatus.Approved, payment.Status);
    }

    [Fact]
    public async Task Staff_cancellation_follows_the_same_window()
    {
        var s = await SetupAsync();
        var late = await OnlinePaidAsync(s, Now.AddHours(3));
        var early = await OnlinePaidAsync(s, Local(2026, 11, 24, 11, 0));
        var barberClient = factory.ClientWithToken(factory.TokenFor(s.Barber));
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var preview = await barberClient.GetFromJsonAsync<JsonElement>($"/appointments/{late}/cancellation-preview");
        Assert.Contains("No se cumple la anticipación de 4 horas", preview.GetProperty("message").GetString());

        var byBarber = await (await barberClient.PostAsync($"/appointments/{late}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();
        var byAdmin = await (await admin.PostAsync($"/appointments/{early}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("paid", byBarber.GetProperty("paymentStatus").GetString());
        Assert.Equal("barber", byBarber.GetProperty("cancelledByRole").GetString());
        Assert.Equal("refunded", byAdmin.GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task Manual_unpaid_and_awaiting_payment_appointments_are_cancelled_without_money()
    {
        var s = await SetupAsync();
        var manual = await AddAsync(s, Now.AddHours(1), a => { a.CreatedByRole = UserRole.Barber; a.PaymentMethod = PaymentMethod.Cash; });
        var awaiting = await AddAsync(s, Local(2026, 11, 24, 12, 0), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(20);
            a.PaymentMethod = PaymentMethod.MercadoPago;
        });

        var manualJson = await (await s.Client.PostAsync($"/appointments/{manual}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();
        var awaitingJson = await (await s.Client.PostAsync($"/appointments/{awaiting}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("unpaid", manualJson.GetProperty("paymentStatus").GetString());
        Assert.Equal("cancelled", awaitingJson.GetProperty("status").GetString());
        Assert.Equal("unpaid", awaitingJson.GetProperty("paymentStatus").GetString());
        Assert.Equal(JsonValueKind.Null, awaitingJson.GetProperty("paymentExpiresAt").ValueKind);
    }

    [Fact]
    public async Task Paid_appointment_without_an_approved_gateway_payment_is_not_refunded()
    {
        var s = await SetupAsync();
        var id = await AddAsync(s, Local(2026, 11, 24, 15, 0), a =>
        {
            a.PaymentStatus = PaymentStatus.Paid;
            a.PaymentMethod = PaymentMethod.MercadoPago;
        });

        var json = await (await s.Client.PostAsync($"/appointments/{id}/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("paid", json.GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task Cancellation_permissions_and_final_states()
    {
        var s = await SetupAsync();
        var (_, otherBarber) = await factory.AddBarberAsync();
        var stranger = await factory.ClientForAsync(UserRole.Client);
        var pending = await AddAsync(s, Local(2026, 11, 24, 16, 0));
        var awaiting = await AddAsync(s, Local(2026, 11, 24, 16, 30), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(20);
        });
        var started = await AddAsync(s, Now.AddMinutes(-10));
        var completed = await AddAsync(s, Local(2026, 11, 20, 10, 0), a => a.Status = AppointmentStatus.Completed);

        await HttpAssertions.AssertErrorAsync(await stranger.PostAsync($"/appointments/{pending}/cancel", null), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await factory.ClientWithToken(factory.TokenFor(otherBarber)).PostAsync($"/appointments/{pending}/cancel", null), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await factory.ClientWithToken(factory.TokenFor(s.Barber)).PostAsync($"/appointments/{awaiting}/cancel", null), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await s.Client.PostAsync($"/appointments/{started}/cancel", null), HttpStatusCode.Conflict, "not_cancellable");
        await HttpAssertions.AssertErrorAsync(await s.Client.PostAsync($"/appointments/{completed}/cancel", null), HttpStatusCode.Conflict, "not_cancellable");
        await HttpAssertions.AssertErrorAsync(await s.Client.GetAsync($"/appointments/{completed}/cancellation-preview"), HttpStatusCode.Conflict, "not_cancellable");
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsync($"/appointments/{pending}/cancel", null), HttpStatusCode.Unauthorized, "unauthorized");

        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/appointments/{pending}/cancel", null)).StatusCode);
        await HttpAssertions.AssertErrorAsync(await s.Client.PostAsync($"/appointments/{pending}/cancel", null), HttpStatusCode.Conflict, "not_cancellable");
    }

    [Fact]
    public async Task Client_reschedules_with_enough_time_keeping_price_and_payment()
    {
        var s = await SetupAsync(minutes: 60);
        var id = await OnlinePaidAsync(s, Local(2026, 11, 24, 10, 0));

        var response = await s.Client.PutAsJsonAsync($"/appointments/{id}/reschedule", new
        {
            appointmentDate = Local(2026, 11, 24, 10, 30),
            paymentMethod = "cash",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Local(2026, 11, 24, 10, 30), json.GetProperty("appointmentDate").GetDateTimeOffset());
        Assert.Equal(Local(2026, 11, 24, 11, 30), json.GetProperty("endDate").GetDateTimeOffset());
        Assert.Equal("paid", json.GetProperty("paymentStatus").GetString());
        Assert.Equal("mercadopago", json.GetProperty("paymentMethod").GetString());
        Assert.Equal(1000m, json.GetProperty("price").GetDecimal());
    }

    [Fact]
    public async Task Client_cannot_reschedule_with_less_than_four_hours_but_staff_can()
    {
        var s = await SetupAsync();
        var id = await AddAsync(s, Now.AddHours(3));
        var body = new { appointmentDate = Local(2026, 11, 24, 17, 0) };

        var json = await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{id}/reschedule", body), HttpStatusCode.Conflict, "modification_window_closed");
        Assert.Contains("podés cancelarlo", json.GetProperty("error").GetString());

        var byBarber = await factory.ClientWithToken(factory.TokenFor(s.Barber)).PutAsJsonAsync($"/appointments/{id}/reschedule", body);
        Assert.Equal(HttpStatusCode.OK, byBarber.StatusCode);
    }

    [Fact]
    public async Task Reschedule_checks_availability_ownership_and_state()
    {
        var s = await SetupAsync();
        var (_, otherBarber) = await factory.AddBarberAsync();
        var mine = await AddAsync(s, Local(2026, 11, 24, 15, 0));
        await AddAsync(s, Local(2026, 11, 24, 15, 30), a => a.ClientName = "Otro cliente", clientId: null);
        var awaiting = await AddAsync(s, Local(2026, 11, 24, 16, 0), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(20);
        });
        var stranger = await factory.ClientForAsync(UserRole.Client);

        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{mine}/reschedule", new { appointmentDate = Local(2026, 11, 24, 15, 30) }),
            HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{mine}/reschedule", new { appointmentDate = Local(2026, 11, 24, 13, 0) }),
            HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{awaiting}/reschedule", new { appointmentDate = Local(2026, 11, 24, 18, 0) }),
            HttpStatusCode.Conflict, "not_modifiable");
        await HttpAssertions.AssertErrorAsync(
            await stranger.PutAsJsonAsync($"/appointments/{mine}/reschedule", new { appointmentDate = Local(2026, 11, 24, 18, 0) }),
            HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(
            await factory.ClientWithToken(factory.TokenFor(otherBarber)).PutAsJsonAsync($"/appointments/{mine}/reschedule", new { appointmentDate = Local(2026, 11, 24, 18, 0) }),
            HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{mine}/reschedule", new { }),
            HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Client_reschedule_respects_the_sixty_day_window_and_own_overlaps()
    {
        var s = await SetupAsync();
        await factory.GenerateAsync(s.Barber.Id, new DateOnly(2027, 1, 25), new DateOnly(2027, 1, 25));
        var (_, otherBarber) = await factory.AddBarberAsync();
        var id = await AddAsync(s, Local(2026, 11, 24, 17, 0));
        await factory.AddAppointmentAsync(otherBarber, s.Cut, Local(2026, 11, 24, 18, 0), clientId: s.ClientUser.Id);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{id}/reschedule", new { appointmentDate = Local(2027, 1, 25, 10, 0) }),
            HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(
            await s.Client.PutAsJsonAsync($"/appointments/{id}/reschedule", new { appointmentDate = Local(2026, 11, 24, 18, 0) }),
            HttpStatusCode.Conflict, "client_overlap");
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PutAsJsonAsync($"/appointments/{id}/reschedule", new { appointmentDate = Local(2027, 1, 25, 10, 0) })).StatusCode);
    }

    [Fact]
    public async Task Staff_can_fix_payment_method_and_client_only_on_manual_appointments()
    {
        var s = await SetupAsync();
        var manual = await AddAsync(s, Local(2026, 11, 24, 18, 30), a =>
        {
            a.CreatedByRole = UserRole.Barber;
            a.PaymentMethod = PaymentMethod.Cash;
        }, clientId: null);
        var online = await OnlinePaidAsync(s, Local(2026, 11, 24, 19, 0));
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var registered = await factory.AddUserAsync(UserRole.Client);

        var fixedManual = await admin.PutAsJsonAsync($"/appointments/{manual}/reschedule", new
        {
            appointmentDate = Local(2026, 11, 24, 18, 30),
            paymentMethod = "transfer",
            clientId = registered.Id,
        });
        var onlineChange = await admin.PutAsJsonAsync($"/appointments/{online}/reschedule", new
        {
            appointmentDate = Local(2026, 11, 24, 19, 0),
            paymentMethod = "cash",
        });

        Assert.Equal(HttpStatusCode.OK, fixedManual.StatusCode);
        var json = await fixedManual.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("transfer", json.GetProperty("paymentMethod").GetString());
        Assert.Equal(registered.Id, json.GetProperty("client").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("clientName").ValueKind);
        await HttpAssertions.AssertErrorAsync(onlineChange, HttpStatusCode.BadRequest, "manual_fields_only");
    }

    [Fact]
    public async Task Lifecycle_expires_unpaid_reservations_and_completes_finished_appointments()
    {
        var s = await SetupAsync();
        var expired = await AddAsync(s, Local(2026, 11, 24, 9, 0), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(-1);
        });
        var stillWaiting = await AddAsync(s, Local(2026, 11, 24, 9, 30), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(5);
        });
        var manualDone = await AddAsync(s, Now.AddMinutes(-60), a => { a.CreatedByRole = UserRole.Barber; a.PaymentMethod = PaymentMethod.Cash; });
        var onlineDone = await AddAsync(s, Now.AddMinutes(-30), a =>
        {
            a.PaymentStatus = PaymentStatus.Paid;
            a.PaymentMethod = PaymentMethod.MercadoPago;
            a.PaidAt = Now.AddDays(-1);
        });
        var inProgress = await AddAsync(s, Now.AddMinutes(-10));

        var first = await RunLifecycleAsync();
        var second = await RunLifecycleAsync();

        Assert.True(first.Expired >= 1);
        Assert.Equal(0, second.Expired + second.Completed);
        var rows = await factory.WithDbAsync(db => db.Appointments.AsNoTracking()
            .Where(a => new[] { expired, stillWaiting, manualDone, onlineDone, inProgress }.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id));
        Assert.Equal(AppointmentStatus.Expired, rows[expired].Status);
        Assert.Equal(AppointmentStatus.AwaitingPayment, rows[stillWaiting].Status);
        Assert.Equal(AppointmentStatus.Completed, rows[manualDone].Status);
        Assert.Equal(PaymentStatus.Paid, rows[manualDone].PaymentStatus);
        Assert.Equal(Now, rows[manualDone].PaidAt);
        Assert.Equal(AppointmentStatus.Completed, rows[onlineDone].Status);
        Assert.Equal(Now.AddDays(-1), rows[onlineDone].PaidAt);
        Assert.Equal(AppointmentStatus.Pending, rows[inProgress].Status);
        Assert.Contains("09:00", await TimesAsync(s.Barber.Id, s.Cut.Id));
    }

    private sealed record Setup(User Barber, Cut Cut, User ClientUser, HttpClient Client);

    private async Task<Setup> SetupAsync(int minutes = 30)
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        await factory.SetSlotsAsync(barber.Id, Enumerable.Range(0, 6).Select(i => Now.AddHours(-1).AddMinutes(30 * i)).Where(t => t.Minute % 30 == 0));
        var cut = await factory.AddCutAsync(minutes);
        var client = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(client.Id, u => u.EmailVerified = true);
        return new Setup(barber, cut, client, factory.ClientWithToken(factory.TokenFor(client)));
    }

    private Task<int> AddAsync(Setup s, DateTimeOffset start, Action<Appointment>? change = null, int? clientId = -1) =>
        factory.WithDbAsync(async db =>
        {
            var appointment = new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(s.Cut.DurationMinutes),
                Status = AppointmentStatus.Pending,
                ClientId = clientId == -1 ? s.ClientUser.Id : clientId,
                ClientName = clientId == -1 ? null : "Cliente sin cuenta",
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

    private async Task<LifecycleResult> RunLifecycleAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppointmentLifecycle>().RunAsync();
    }

    private async Task<List<string>> TimesAsync(int barberId, int cutId)
    {
        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/appointments/availability?barberId={barberId}&cutId={cutId}&date={Tuesday:yyyy-MM-dd}");
        return json.GetProperty("times").EnumerateArray().Select(t => t.GetProperty("time").GetString()!).ToList();
    }
}
