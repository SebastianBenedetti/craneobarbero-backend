using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Appointments;

public sealed class BookingTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Client_books_an_appointment_that_waits_for_payment()
    {
        var (branch, barber) = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var (client, user) = await VerifiedClientAsync();
        var start = Local(2026, 11, 24, 10, 0);

        var response = await client.PostAsJsonAsync("/appointments", new
        {
            barberId = barber.Id,
            cutId = cut.Id,
            branchId = branch.Id,
            appointmentDate = start,
            clientId = 999,
            status = "pending",
            price = 1,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, json.GetProperty("checkoutUrl").ValueKind);
        var appointment = json.GetProperty("appointment");
        Assert.Equal("awaiting_payment", appointment.GetProperty("status").GetString());
        Assert.Equal("unpaid", appointment.GetProperty("paymentStatus").GetString());
        Assert.Equal("mercadopago", appointment.GetProperty("paymentMethod").GetString());
        Assert.Equal(Now.AddMinutes(30), appointment.GetProperty("paymentExpiresAt").GetDateTimeOffset());
        Assert.Equal(start.AddMinutes(30), appointment.GetProperty("endDate").GetDateTimeOffset());
        Assert.Equal(1000m, appointment.GetProperty("price").GetDecimal());
        Assert.Equal(user.Id, appointment.GetProperty("client").GetProperty("id").GetInt32());
        Assert.Equal(branch.Id, appointment.GetProperty("branch").GetProperty("id").GetInt32());
        Assert.Equal("client", appointment.GetProperty("createdByRole").GetString());

        var times = await TimesAsync(barber.Id, cut.Id);
        Assert.DoesNotContain("10:00", times);
    }

    [Fact]
    public async Task Same_time_cannot_be_booked_twice_and_overlap_counts_the_duration()
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var hour = await factory.AddCutAsync(60);
        var half = await factory.AddCutAsync(30);
        var (first, _) = await VerifiedClientAsync();
        var (second, _) = await VerifiedClientAsync();

        var booked = await first.PostAsJsonAsync("/appointments", Body(barber, hour, Local(2026, 11, 24, 10, 0)));
        var sameTime = await second.PostAsJsonAsync("/appointments", Body(barber, half, Local(2026, 11, 24, 10, 0)));
        var insideDuration = await second.PostAsJsonAsync("/appointments", Body(barber, half, Local(2026, 11, 24, 10, 30)));
        var startsBefore = await second.PostAsJsonAsync("/appointments", Body(barber, hour, Local(2026, 11, 24, 9, 30)));
        var right_after = await second.PostAsJsonAsync("/appointments", Body(barber, half, Local(2026, 11, 24, 11, 0)));

        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var error = await HttpAssertions.AssertErrorAsync(sameTime, HttpStatusCode.Conflict, "slot_unavailable");
        Assert.Equal("El horario ya no está disponible. Elegí otro.", error.GetProperty("error").GetString());
        await HttpAssertions.AssertErrorAsync(insideDuration, HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(startsBefore, HttpStatusCode.Conflict, "slot_unavailable");
        Assert.Equal(HttpStatusCode.Created, right_after.StatusCode);
    }

    [Fact]
    public async Task Client_cannot_have_two_overlapping_appointments()
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var (_, other) = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var (client, _) = await VerifiedClientAsync();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/appointments", Body(barber, cut, Local(2026, 11, 24, 16, 0)))).StatusCode);
        var response = await client.PostAsJsonAsync("/appointments", Body(other, cut, Local(2026, 11, 24, 16, 0)));

        var json = await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "client_overlap");
        Assert.Equal("Ya tenés otro turno en ese horario.", json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(13, 0)]
    [InlineData(10, 15)]
    [InlineData(20, 0)]
    [InlineData(8, 30)]
    public async Task Times_outside_the_opening_hours_or_off_the_grid_are_rejected(int hour, int minute)
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var (client, _) = await VerifiedClientAsync();

        var response = await client.PostAsJsonAsync("/appointments", Body(barber, cut, Local(2026, 11, 24, hour, minute)));

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "slot_unavailable");
    }

    [Fact]
    public async Task Sixty_minute_service_at_12_30_is_rejected_and_at_12_00_accepted()
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var hour = await factory.AddCutAsync(60);
        var (client, _) = await VerifiedClientAsync();

        var late = await client.PostAsJsonAsync("/appointments", Body(barber, hour, Local(2026, 11, 24, 12, 30)));
        var onTime = await client.PostAsJsonAsync("/appointments", Body(barber, hour, Local(2026, 11, 24, 12, 0)));

        await HttpAssertions.AssertErrorAsync(late, HttpStatusCode.Conflict, "slot_unavailable");
        Assert.Equal(HttpStatusCode.Created, onTime.StatusCode);
    }

    [Fact]
    public async Task Sunday_past_and_beyond_sixty_days_are_rejected_for_clients()
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, new DateOnly(2027, 1, 25), new DateOnly(2027, 1, 25));
        await factory.SetSlotsAsync(barber.Id, [Local(2026, 11, 22, 10, 0)]);
        var cut = await factory.AddCutAsync(30);
        var (client, _) = await VerifiedClientAsync();
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var sunday = await client.PostAsJsonAsync("/appointments", Body(barber, cut, Local(2026, 11, 29, 10, 0)));
        var past = await client.PostAsJsonAsync("/appointments", Body(barber, cut, Local(2026, 11, 22, 10, 0)));
        var far = await client.PostAsJsonAsync("/appointments", Body(barber, cut, Local(2027, 1, 25, 10, 0)));
        var adminFar = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = barber.Id, cutId = cut.Id, appointmentDate = Local(2027, 1, 25, 10, 0), clientName = "Juan", paymentMethod = "cash",
        });

        await HttpAssertions.AssertErrorAsync(sunday, HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(past, HttpStatusCode.Conflict, "slot_unavailable");
        await HttpAssertions.AssertErrorAsync(far, HttpStatusCode.Conflict, "slot_unavailable");
        Assert.Equal(HttpStatusCode.Created, adminFar.StatusCode);
    }

    [Fact]
    public async Task Unverified_clients_and_other_roles_cannot_book_online()
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var unverified = await factory.ClientForAsync(UserRole.Client);
        var body = Body(barber, cut, Local(2026, 11, 24, 15, 0));

        await HttpAssertions.AssertErrorAsync(await unverified.PostAsJsonAsync("/appointments", body), HttpStatusCode.Forbidden, "email_not_verified");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Barber)).PostAsJsonAsync("/appointments", body), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Admin)).PostAsJsonAsync("/appointments", body), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsJsonAsync("/appointments", body), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Inactive_barber_inactive_service_or_wrong_branch_are_rejected()
    {
        var (_, barber) = await BarberWithSlotsAsync();
        var (otherBranch, _) = await factory.AddBarberAsync();
        var (_, inactive) = await factory.AddBarberAsync(isActive: false);
        var cut = await factory.AddCutAsync(30);
        var inactiveCut = await factory.AddCutAsync(30, isActive: false);
        var (client, _) = await VerifiedClientAsync();
        var start = Local(2026, 11, 24, 17, 30);

        await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/appointments", Body(inactive, cut, start)), HttpStatusCode.BadRequest, "barber_unavailable");
        await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/appointments", Body(barber, inactiveCut, start)), HttpStatusCode.BadRequest, "cut_unavailable");
        await HttpAssertions.AssertErrorAsync(
            await client.PostAsJsonAsync("/appointments", new { barberId = barber.Id, cutId = cut.Id, appointmentDate = start, branchId = otherBranch.Id }),
            HttpStatusCode.BadRequest, "barber_unavailable");
        var missing = await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/appointments", new { }), HttpStatusCode.BadRequest, "validation_error");
        Assert.True(missing.GetProperty("errors").TryGetProperty("appointmentDate", out _));
    }

    [Fact]
    public async Task Generic_update_and_delete_of_appointments_do_not_exist()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var put = await admin.PutAsJsonAsync("/appointments/1", new { status = "completed" });
        var delete = await admin.DeleteAsync("/appointments/1");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
    }

    private async Task<(Branch Branch, User Barber)> BarberWithSlotsAsync()
    {
        var created = await factory.AddBarberAsync();
        await factory.GenerateAsync(created.Barber.Id, Tuesday, Tuesday);
        return created;
    }

    private async Task<(HttpClient Client, User User)> VerifiedClientAsync()
    {
        var user = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(user.Id, u => u.EmailVerified = true);
        return (factory.ClientWithToken(factory.TokenFor(user)), user);
    }

    private static object Body(User barber, Cut cut, DateTimeOffset start) =>
        new { barberId = barber.Id, cutId = cut.Id, appointmentDate = start };

    private async Task<List<string>> TimesAsync(int barberId, int cutId)
    {
        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/appointments/availability?barberId={barberId}&cutId={cutId}&date={Tuesday:yyyy-MM-dd}");
        return json.GetProperty("times").EnumerateArray().Select(t => t.GetProperty("time").GetString()!).ToList();
    }
}
