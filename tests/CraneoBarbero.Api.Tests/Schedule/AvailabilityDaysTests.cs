using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class AvailabilityDaysTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    [Fact]
    public async Task Each_day_reports_free_times_and_its_status()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var holidayDate = new DateOnly(2026, 11, 27);
        await factory.WithDbAsync(async db =>
        {
            db.Holidays.Add(new Holiday { Date = holidayDate, Name = "Día de prueba", Type = HolidayType.Custom, Source = "manual", IsActive = true });
            return await db.SaveChangesAsync();
        });
        await factory.GenerateAsync(barber.Id, new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 29));
        for (var i = 0; i < 18; i++)
        {
            var start = i < 8 ? Local(2026, 11, 26, 9).AddMinutes(30 * i) : Local(2026, 11, 26, 15).AddMinutes(30 * (i - 8));
            await factory.AddAppointmentAsync(barber, cut, start);
        }

        var json = await DaysAsync(factory.CreateClient(), barber.Id, cut.Id, "2026-11-24", "2026-11-29");
        var days = json.GetProperty("days").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!);

        Assert.Equal(6, days.Count);
        Assert.Equal(("available", 18), Read(days["2026-11-24"]));
        Assert.Equal(("full", 0), Read(days["2026-11-26"]));
        Assert.Equal(("holiday", 0), Read(days["2026-11-27"]));
        Assert.Equal("Día de prueba", days["2026-11-27"].GetProperty("holidayName").GetString());
        Assert.Equal(("available", 12), Read(days["2026-11-28"]));
        Assert.Equal(("closed", 0), Read(days["2026-11-29"]));
        Assert.Equal(30, json.GetProperty("durationMinutes").GetInt32());
    }

    [Fact]
    public async Task Clients_get_sixty_days_and_staff_get_the_whole_range()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        await factory.GenerateAsync(barber.Id, new DateOnly(2027, 1, 20), new DateOnly(2027, 1, 31));
        var client = await factory.ClientForAsync(UserRole.Client);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var forClient = await DaysAsync(client, barber.Id, cut.Id, "2027-01-20", "2027-01-31");
        var forAnonymous = await DaysAsync(factory.CreateClient(), barber.Id, cut.Id, "2027-01-20", "2027-01-31");
        var forAdmin = await DaysAsync(admin, barber.Id, cut.Id, "2027-01-20", "2027-01-31");

        Assert.Equal("2027-01-22", forClient.GetProperty("lastBookableDate").GetString());
        Assert.Equal("2027-01-22", forClient.GetProperty("days").EnumerateArray().Last().GetProperty("date").GetString());
        Assert.Equal(3, forAnonymous.GetProperty("days").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, forAdmin.GetProperty("lastBookableDate").ValueKind);
        Assert.Equal(12, forAdmin.GetProperty("days").GetArrayLength());
        Assert.Equal(("available", 18), Read(forAdmin.GetProperty("days")[8]));
        Assert.Equal(("closed", 0), Read(forAdmin.GetProperty("days")[11]));
    }

    [Fact]
    public async Task Past_days_are_left_out()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);

        var json = await DaysAsync(factory.CreateClient(), barber.Id, cut.Id, "2026-11-20", "2026-11-24");

        Assert.Equal(["2026-11-23", "2026-11-24"],
            json.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()!).ToArray());
    }

    [Fact]
    public async Task Invalid_ranges_and_unknown_barbers_are_rejected()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var (_, inactive) = await factory.AddBarberAsync(isActive: false);
        var cut = await factory.AddCutAsync(30);
        var anonymous = factory.CreateClient();

        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync(Url(barber.Id, cut.Id, "2026-11-24", "2027-02-01")), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync(Url(barber.Id, cut.Id, "2026-11-24", "2026-11-23")), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync($"/appointments/availability/days?barberId={barber.Id}&cutId={cut.Id}"), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await anonymous.GetAsync(Url(inactive.Id, cut.Id, "2026-11-24", "2026-11-25")), HttpStatusCode.NotFound, "not_found");
    }

    private static (string Status, int Available) Read(JsonElement day) =>
        (day.GetProperty("status").GetString()!, day.GetProperty("available").GetInt32());

    private static string Url(int barberId, int cutId, string from, string to) =>
        $"/appointments/availability/days?barberId={barberId}&cutId={cutId}&from={from}&to={to}";

    private static async Task<JsonElement> DaysAsync(HttpClient client, int barberId, int cutId, string from, string to)
    {
        var response = await client.GetAsync(Url(barberId, cutId, from, to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
