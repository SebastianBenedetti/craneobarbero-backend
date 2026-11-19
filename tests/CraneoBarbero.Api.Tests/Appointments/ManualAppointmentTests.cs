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

public sealed class ManualAppointmentTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);

    [Fact]
    public async Task Barber_creates_a_pending_unpaid_appointment_for_himself()
    {
        var barber = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var client = factory.ClientWithToken(factory.TokenFor(barber));

        var response = await client.PostAsJsonAsync("/appointments/manual", new
        {
            cutId = cut.Id, appointmentDate = Local(2026, 11, 24, 9, 0), clientName = "  Juan Pérez ", paymentMethod = "cash",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", json.GetProperty("status").GetString());
        Assert.Equal("unpaid", json.GetProperty("paymentStatus").GetString());
        Assert.Equal("cash", json.GetProperty("paymentMethod").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("paymentExpiresAt").ValueKind);
        Assert.Equal("Juan Pérez", json.GetProperty("clientName").GetString());
        Assert.Equal(barber.Id, json.GetProperty("barber").GetProperty("id").GetInt32());
        Assert.Equal("barber", json.GetProperty("createdByRole").GetString());
    }

    [Fact]
    public async Task Barber_cannot_create_appointments_for_another_barber()
    {
        var barber = await BarberWithSlotsAsync();
        var other = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);

        var response = await factory.ClientWithToken(factory.TokenFor(barber)).PostAsJsonAsync("/appointments/manual", new
        {
            barberId = other.Id, cutId = cut.Id, appointmentDate = Local(2026, 11, 24, 9, 0), clientName = "Juan", paymentMethod = "cash",
        });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Selected_client_prevails_over_the_typed_name()
    {
        var barber = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var registered = await factory.AddUserAsync(UserRole.Client);
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = barber.Id, cutId = cut.Id, appointmentDate = Local(2026, 11, 24, 11, 0),
            clientId = registered.Id, clientName = "Otro nombre", paymentMethod = "transfer",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(registered.Id, json.GetProperty("client").GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("clientName").ValueKind);
        Assert.Equal("admin", json.GetProperty("createdByRole").GetString());

        var registeredClient = factory.ClientWithToken(factory.TokenFor(registered));
        var mine = await registeredClient.GetFromJsonAsync<JsonElement>("/appointments/my");
        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        Assert.Equal("unpaid", mine.GetProperty("items")[0].GetProperty("paymentStatus").GetString());
    }

    [Fact]
    public async Task Manual_appointment_requires_client_or_name_and_an_offline_payment_method()
    {
        var barber = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var client = factory.ClientWithToken(factory.TokenFor(barber));
        var start = Local(2026, 11, 24, 15, 0);

        var noClient = await client.PostAsJsonAsync("/appointments/manual", new { cutId = cut.Id, appointmentDate = start, paymentMethod = "cash" });
        var online = await client.PostAsJsonAsync("/appointments/manual", new { cutId = cut.Id, appointmentDate = start, clientName = "Ana", paymentMethod = "mercadopago" });
        var noMethod = await client.PostAsJsonAsync("/appointments/manual", new { cutId = cut.Id, appointmentDate = start, clientName = "Ana" });

        Assert.True((await HttpAssertions.AssertErrorAsync(noClient, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("clientName", out _));
        Assert.True((await HttpAssertions.AssertErrorAsync(online, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("paymentMethod", out _));
        Assert.True((await HttpAssertions.AssertErrorAsync(noMethod, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("paymentMethod", out _));
    }

    [Fact]
    public async Task Admin_must_choose_a_barber_and_an_active_registered_client()
    {
        var barber = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var inactiveClient = await factory.AddUserAsync(UserRole.Client, isActive: false);
        var otherBarber = await factory.AddUserAsync(UserRole.Barber);
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var start = Local(2026, 11, 24, 16, 0);

        var noBarber = await admin.PostAsJsonAsync("/appointments/manual", new { cutId = cut.Id, appointmentDate = start, clientName = "Ana", paymentMethod = "card" });
        var inactive = await admin.PostAsJsonAsync("/appointments/manual", new { barberId = barber.Id, cutId = cut.Id, appointmentDate = start, clientId = inactiveClient.Id, paymentMethod = "card" });
        var notClient = await admin.PostAsJsonAsync("/appointments/manual", new { barberId = barber.Id, cutId = cut.Id, appointmentDate = start, clientId = otherBarber.Id, paymentMethod = "card" });

        Assert.True((await HttpAssertions.AssertErrorAsync(noBarber, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("barberId", out _));
        await HttpAssertions.AssertErrorAsync(inactive, HttpStatusCode.BadRequest, "client_unavailable");
        await HttpAssertions.AssertErrorAsync(notClient, HttpStatusCode.BadRequest, "client_unavailable");
    }

    [Fact]
    public async Task Manual_appointments_follow_the_same_overlap_rules()
    {
        var barber = await BarberWithSlotsAsync();
        var other = await BarberWithSlotsAsync();
        var cut = await factory.AddCutAsync(30);
        var registered = await factory.AddUserAsync(UserRole.Client);
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var start = Local(2026, 11, 24, 18, 0);

        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = barber.Id, cutId = cut.Id, appointmentDate = start, clientId = registered.Id, paymentMethod = "cash",
        })).StatusCode);
        var sameBarber = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = barber.Id, cutId = cut.Id, appointmentDate = start, clientName = "Otro", paymentMethod = "cash",
        });
        var sameClient = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = other.Id, cutId = cut.Id, appointmentDate = start, clientId = registered.Id, paymentMethod = "cash",
        });
        var outsideHours = await admin.PostAsJsonAsync("/appointments/manual", new
        {
            barberId = barber.Id, cutId = cut.Id, appointmentDate = Local(2026, 11, 24, 21, 0), clientName = "Otro", paymentMethod = "cash",
        });

        await HttpAssertions.AssertErrorAsync(sameBarber, HttpStatusCode.Conflict, "slot_unavailable");
        var json = await HttpAssertions.AssertErrorAsync(sameClient, HttpStatusCode.Conflict, "client_overlap");
        Assert.Equal("El cliente ya tiene otro turno en ese horario.", json.GetProperty("error").GetString());
        await HttpAssertions.AssertErrorAsync(outsideHours, HttpStatusCode.Conflict, "slot_unavailable");
    }

    [Fact]
    public async Task Clients_cannot_create_manual_appointments()
    {
        var client = await factory.ClientForAsync(UserRole.Client);

        var response = await client.PostAsJsonAsync("/appointments/manual", new { });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    private async Task<User> BarberWithSlotsAsync()
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        return barber;
    }
}
