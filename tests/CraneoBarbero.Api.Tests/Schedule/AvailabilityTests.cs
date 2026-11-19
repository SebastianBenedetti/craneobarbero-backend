using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class AvailabilityTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private static readonly DateOnly Saturday = new(2026, 11, 28);
    private static readonly DateOnly Sunday = new(2026, 11, 29);

    [Fact]
    public async Task Sixty_minute_service_must_end_inside_the_opening_range()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var cut = await factory.AddCutAsync(60);

        var times = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Tuesday);

        Assert.Contains("12:00", times);
        Assert.DoesNotContain("12:30", times);
        Assert.Contains("19:00", times);
        Assert.DoesNotContain("19:30", times);
        Assert.Equal("09:00", times.First());
    }

    [Fact]
    public async Task Forty_five_minute_service_needs_two_consecutive_blocks()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var cut = await factory.AddCutAsync(45);

        var times = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Tuesday);

        Assert.Contains("12:00", times);
        Assert.DoesNotContain("12:30", times);
        Assert.Equal(16, times.Count);
    }

    [Fact]
    public async Task Saturday_last_start_for_thirty_minutes_is_14_30()
    {
        var barber = await GeneratedBarberAsync(Saturday);
        var cut = await factory.AddCutAsync(30);

        var times = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Saturday);

        Assert.Equal("14:30", times.Last());
        Assert.DoesNotContain("15:00", times);
        Assert.Equal(12, times.Count);
    }

    [Fact]
    public async Task Sunday_has_nothing_until_the_admin_adds_slots()
    {
        var barber = await GeneratedBarberAsync(Sunday);
        var cut = await factory.AddCutAsync(30);

        Assert.Empty(await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Sunday));

        await factory.SetSlotsAsync(barber.Id, Enumerable.Range(0, 8).Select(i => Local(2026, 11, 29, 10).AddMinutes(30 * i)));

        var times = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Sunday);
        Assert.Equal(["10:00", "10:30", "11:00", "11:30", "12:00", "12:30", "13:00", "13:30"], times);
    }

    [Fact]
    public async Task Busy_barber_does_not_affect_other_barbers_of_the_branch()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        var other = await factory.AddBarberInBranchAsync(branch.Id);
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        await factory.GenerateAsync(other.Id, Tuesday, Tuesday);
        var cut = await factory.AddCutAsync(30);
        var hour = await factory.AddCutAsync(60);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 24, 10, 0));

        var busy = await TimesAsync(factory.CreateClient(), barber.Id, hour.Id, Tuesday);
        var free = await TimesAsync(factory.CreateClient(), other.Id, hour.Id, Tuesday);

        Assert.DoesNotContain("10:00", busy);
        Assert.DoesNotContain("09:30", busy);
        Assert.Contains("09:00", busy);
        Assert.Contains("10:30", busy);
        Assert.Contains("10:00", free);
        Assert.Contains("09:30", free);
    }

    [Fact]
    public async Task Cancelled_and_expired_appointments_free_the_time()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var cut = await factory.AddCutAsync(30);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 24, 11, 0), AppointmentStatus.Cancelled);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 24, 11, 30), AppointmentStatus.Expired);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 24, 12, 0), AppointmentStatus.AwaitingPayment);

        var times = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Tuesday);

        Assert.Contains("11:00", times);
        Assert.Contains("11:30", times);
        Assert.DoesNotContain("12:00", times);
    }

    [Fact]
    public async Task Removed_slot_breaks_a_longer_service()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var hour = await factory.AddCutAsync(60);
        var removedAt = Local(2026, 11, 24, 16, 0);
        await factory.WithDbAsync(async db =>
        {
            var slot = await db.AvailableSlots.SingleAsync(s => s.BarberId == barber.Id && s.StartAt == removedAt);
            slot.DeletedAt = Now;
            return await db.SaveChangesAsync();
        });

        var times = await TimesAsync(factory.CreateClient(), barber.Id, hour.Id, Tuesday);

        Assert.DoesNotContain("15:30", times);
        Assert.DoesNotContain("16:00", times);
        Assert.Contains("15:00", times);
        Assert.Contains("16:30", times);
    }

    [Fact]
    public async Task Clients_only_see_sixty_days_ahead_but_the_admin_sees_everything()
    {
        var far = new DateOnly(2027, 1, 25);
        var near = new DateOnly(2027, 1, 20);
        var barber = await GeneratedBarberAsync(near, far);
        var cut = await factory.AddCutAsync(30);
        var client = await factory.ClientForAsync(UserRole.Client);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        Assert.Empty(await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, far));
        Assert.Empty(await TimesAsync(client, barber.Id, cut.Id, far));
        Assert.Equal(18, (await TimesAsync(admin, barber.Id, cut.Id, far)).Count);
        Assert.Equal(18, (await TimesAsync(client, barber.Id, cut.Id, near)).Count);
    }

    [Fact]
    public async Task Past_days_and_past_hours_are_not_offered()
    {
        var barber = await GeneratedBarberAsync(new DateOnly(2026, 11, 23));
        var cut = await factory.AddCutAsync(30);
        factory.Time.SetUtcNow(Local(2026, 11, 23, 12, 10));
        try
        {
            var today = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, new DateOnly(2026, 11, 23));
            var yesterday = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, new DateOnly(2026, 11, 22));

            Assert.Equal("12:30", today.First());
            Assert.Empty(yesterday);
        }
        finally
        {
            factory.Time.SetUtcNow(Now);
        }
    }

    [Fact]
    public async Task Client_with_another_appointment_at_that_time_does_not_see_it()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var (_, otherBarber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var clientUser = await factory.AddUserAsync(UserRole.Client);
        await factory.AddAppointmentAsync(otherBarber, cut, Local(2026, 11, 24, 17, 0), clientId: clientUser.Id);

        var mine = await TimesAsync(factory.ClientWithToken(factory.TokenFor(clientUser)), barber.Id, cut.Id, Tuesday);
        var anonymous = await TimesAsync(factory.CreateClient(), barber.Id, cut.Id, Tuesday);

        Assert.DoesNotContain("17:00", mine);
        Assert.Contains("17:00", anonymous);
    }

    [Fact]
    public async Task Response_has_the_utc_instant_and_local_time()
    {
        var barber = await GeneratedBarberAsync(Tuesday);
        var cut = await factory.AddCutAsync(30);

        var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>(Url(barber.Id, cut.Id, Tuesday));

        Assert.Equal(30, json.GetProperty("durationMinutes").GetInt32());
        Assert.Equal("2026-11-24", json.GetProperty("date").GetString());
        var first = json.GetProperty("times")[0];
        Assert.Equal("09:00", first.GetProperty("time").GetString());
        Assert.Equal(Local(2026, 11, 24, 9, 0), first.GetProperty("startAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Inactive_barber_or_service_and_missing_parameters_are_rejected()
    {
        var (_, inactive) = await factory.AddBarberAsync(isActive: false);
        var barber = await GeneratedBarberAsync(Tuesday);
        var cut = await factory.AddCutAsync(30);
        var inactiveCut = await factory.AddCutAsync(30, isActive: false);
        var anonymous = factory.CreateClient();

        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync(Url(inactive.Id, cut.Id, Tuesday)), HttpStatusCode.NotFound, "not_found");
        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync(Url(barber.Id, inactiveCut.Id, Tuesday)), HttpStatusCode.NotFound, "not_found");
        var json = await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync("/appointments/availability?date=24-11-2026"), HttpStatusCode.BadRequest, "validation_error");
        Assert.True(json.TryGetProperty("errors", out _));
    }

    private async Task<User> GeneratedBarberAsync(DateOnly from, DateOnly? to = null)
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, from, to ?? from);
        return barber;
    }

    private static string Url(int barberId, int cutId, DateOnly date) =>
        $"/appointments/availability?barberId={barberId}&cutId={cutId}&date={date:yyyy-MM-dd}";

    private static async Task<List<string>> TimesAsync(HttpClient client, int barberId, int cutId, DateOnly date)
    {
        var response = await client.GetAsync(Url(barberId, cutId, date));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("times").EnumerateArray().Select(t => t.GetProperty("time").GetString()!).ToList();
    }
}
