using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class HolidayTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    [Fact]
    public async Task Sync_saves_national_holidays_and_closes_only_free_slots()
    {
        var day = new DateOnly(2026, 12, 8);
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, day, day);
        var cut = await factory.AddCutAsync(30);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 8, 10, 0));
        factory.Holidays.Years[2026] = [new ExternalHoliday(day, "Inmaculada Concepción de María")];
        factory.Holidays.Years[2027] = [new ExternalHoliday(new DateOnly(2027, 1, 1), "Año nuevo")];

        var result = await factory.SyncHolidaysAsync();

        Assert.Equal(2, result.Added);
        Assert.Empty(result.FailedYears);
        Assert.Equal([day], result.DaysWithAppointments);
        var slots = await factory.SlotsAsync(barber.Id, day);
        Assert.Equal(17, slots.Count(s => s.DeletedAt != null && s.DeleteReason == HolidayService.HolidayReason));
        Assert.Null(slots.Single(s => s.StartAt == Local(2026, 12, 8, 10, 0)).DeletedAt);
        var stored = await factory.WithDbAsync(db => db.Holidays.AsNoTracking().SingleAsync(h => h.Date == day));
        Assert.Equal(HolidayType.National, stored.Type);
        Assert.Equal("api", stored.Source);

        var again = await factory.SyncHolidaysAsync();
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.SlotsRemoved);
    }

    [Fact]
    public async Task Sync_reports_failed_years_and_removes_holidays_the_source_no_longer_has()
    {
        var moved = new DateOnly(2027, 3, 1);
        await factory.WithDbAsync(async db =>
        {
            db.Holidays.Add(new Holiday { Date = moved, Name = "Trasladable viejo", Type = HolidayType.National, Source = "api" });
            db.Holidays.Add(new Holiday { Date = new DateOnly(2027, 3, 2), Name = "Cierre propio", Type = HolidayType.Custom, Source = "manual" });
            return await db.SaveChangesAsync();
        });
        factory.Holidays.Failing.Add(2026);
        factory.Holidays.Years[2027] = [new ExternalHoliday(new DateOnly(2027, 1, 1), "Año nuevo")];
        try
        {
            var result = await factory.SyncHolidaysAsync();

            Assert.Equal([2026], result.FailedYears);
            Assert.Equal(1, result.Removed);
            Assert.False(await factory.WithDbAsync(db => db.Holidays.AnyAsync(h => h.Date == moved)));
            Assert.True(await factory.WithDbAsync(db => db.Holidays.AnyAsync(h => h.Date == new DateOnly(2027, 3, 2))));
        }
        finally
        {
            factory.Holidays.Failing.Clear();
        }
    }

    [Fact]
    public async Task Admin_adds_a_custom_closed_day_and_can_reopen_it()
    {
        var day = new DateOnly(2026, 12, 23);
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, day, day);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var created = await admin.PostAsJsonAsync("/holidays", new { date = "2026-12-23", name = "Cierre por inventario" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("holiday").GetProperty("id").GetInt32();
        Assert.Equal("custom", body.GetProperty("holiday").GetProperty("type").GetString());
        Assert.True(body.GetProperty("slotsRemoved").GetInt32() >= 18);
        Assert.All(await factory.SlotsAsync(barber.Id, day), s => Assert.NotNull(s.DeletedAt));

        var duplicated = await admin.PostAsJsonAsync("/holidays", new { date = "2026-12-23", name = "Otra vez" });
        await HttpAssertions.AssertErrorAsync(duplicated, HttpStatusCode.Conflict, "holiday_exists");

        var reopened = await admin.PutAsJsonAsync($"/holidays/{id}", new { name = "Cierre por inventario", isActive = false });

        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
        var reopenedBody = await reopened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(reopenedBody.GetProperty("holiday").GetProperty("isActive").GetBoolean());
        Assert.True(reopenedBody.GetProperty("slotsRestored").GetInt32() >= 18);
        Assert.All(await factory.SlotsAsync(barber.Id, day), s => Assert.Null(s.DeletedAt));

        var closedAgain = await admin.PutAsJsonAsync($"/holidays/{id}", new { name = "Cierre por inventario", isActive = true });
        Assert.Equal(HttpStatusCode.OK, closedAgain.StatusCode);
        Assert.All(await factory.SlotsAsync(barber.Id, day), s => Assert.NotNull(s.DeletedAt));
    }

    [Fact]
    public async Task Deactivating_a_holiday_creates_the_slots_that_were_never_generated()
    {
        var day = new DateOnly(2026, 12, 9);
        var (_, barber) = await factory.AddBarberAsync();
        var holiday = await factory.WithDbAsync(async db =>
        {
            var h = new Holiday { Date = day, Name = "Feriado de prueba", Type = HolidayType.National, Source = "api" };
            db.Holidays.Add(h);
            await db.SaveChangesAsync();
            return h;
        });
        await factory.GenerateAsync(barber.Id, day, day);
        Assert.Empty(await factory.SlotsAsync(barber.Id, day));
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PutAsJsonAsync($"/holidays/{holiday.Id}", new { name = "Feriado de prueba", isActive = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(18, (await factory.SlotsAsync(barber.Id, day)).Count);
    }

    [Fact]
    public async Task Holiday_list_shows_appointments_of_the_day()
    {
        var day = new DateOnly(2026, 12, 10);
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 10, 15, 0));
        await factory.WithDbAsync(async db =>
        {
            db.Holidays.Add(new Holiday { Date = day, Name = "Día con turnos", Type = HolidayType.Custom, Source = "manual" });
            return await db.SaveChangesAsync();
        });
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var list = await admin.GetFromJsonAsync<JsonElement>("/holidays?year=2026");

        var row = list.EnumerateArray().Single(h => h.GetProperty("date").GetString() == "2026-12-10");
        Assert.Equal(1, row.GetProperty("appointmentsCount").GetInt32());
    }

    [Fact]
    public async Task Holiday_validation_and_permissions()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var past = await admin.PostAsJsonAsync("/holidays", new { date = "2026-11-01", name = "Pasado" });
        var noName = await admin.PostAsJsonAsync("/holidays", new { date = "2026-12-30", name = "" });

        var errors = (await HttpAssertions.AssertErrorAsync(past, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("date", out _));
        await HttpAssertions.AssertErrorAsync(noName, HttpStatusCode.BadRequest, "validation_error");

        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.GetAsync("/holidays"), HttpStatusCode.Forbidden, "forbidden");
            await HttpAssertions.AssertErrorAsync(await client.PostAsync("/holidays/sync", null), HttpStatusCode.Forbidden, "forbidden");
        }

        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync("/holidays"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Argentina_datos_source_keeps_only_non_working_national_holidays()
    {
        const string payload = """
            [
              { "fecha": "2026-01-01", "tipo": "inamovible", "nombre": "Año nuevo" },
              { "fecha": "2026-03-23", "tipo": "puente", "nombre": "Puente turístico" },
              { "fecha": "2026-06-15", "tipo": "trasladable", "nombre": "Paso a la Inmortalidad de Güemes" },
              { "fecha": "fecha-rota", "tipo": "inamovible", "nombre": "Inválido" }
            ]
            """;
        var http = new HttpClient(new StubHandler(payload)) { BaseAddress = new Uri("https://example.com/feriados/") };
        var source = new ArgentinaDatosHolidaySource(http);

        var holidays = await source.GetNationalHolidaysAsync(2026, CancellationToken.None);

        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 15)], holidays.Select(h => h.Date));
        Assert.Equal("Año nuevo", holidays[0].Name);
    }

    private sealed class StubHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://example.com/feriados/2026", request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
