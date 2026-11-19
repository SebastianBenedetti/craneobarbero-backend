using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Accounts;

public sealed class UserAdminTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private const string Password = "Clave2026segura";

    [Fact]
    public async Task Users_listing_is_only_for_admins()
    {
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync("/users"), HttpStatusCode.Unauthorized, "unauthorized");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Client)).GetAsync("/users"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Barber)).GetAsync("/users"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Users_listing_is_paged_filtered_and_has_no_passwords()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var marker = $"Buscado{Guid.NewGuid():N}"[..20];
        var active = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(active.Id, u => u.Name = marker + " Activo");
        var inactive = await factory.AddUserAsync(UserRole.Client, isActive: false);
        await factory.UpdateUserAsync(inactive.Id, u => u.Name = marker + " Inactivo");

        var all = await admin.GetFromJsonAsync<JsonElement>($"/users?q={marker}&role=client");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        var first = all.GetProperty("items")[0];
        Assert.False(first.TryGetProperty("password", out _));
        Assert.False(first.TryGetProperty("passwordHash", out _));
        Assert.Equal("client", first.GetProperty("role").GetString());

        var onlyInactive = await admin.GetFromJsonAsync<JsonElement>($"/users?q={marker}&active=false");
        Assert.Equal(inactive.Id, onlyInactive.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());

        var paged = await admin.GetFromJsonAsync<JsonElement>($"/users?q={marker}&pageSize=1&page=2");
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Admin_creates_clients_with_verification_and_admins_already_verified()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var clientEmail = $"Nuevo-{Guid.NewGuid():N}@Example.com";
        var adminEmail = $"admin-{Guid.NewGuid():N}@example.com";

        var created = await admin.PostAsJsonAsync("/users", new { name = "Cliente Nuevo", email = clientEmail, password = Password, phone = "341 555 0000", role = "client" });
        var createdAdmin = await admin.PostAsJsonAsync("/users", new { name = "Admin Nuevo", email = adminEmail, password = Password, role = "admin" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientEmail.ToLowerInvariant(), json.GetProperty("email").GetString());
        Assert.False(json.GetProperty("emailVerified").GetBoolean());
        await factory.Emails.WaitForAsync(clientEmail.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.Created, createdAdmin.StatusCode);
        Assert.True((await createdAdmin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("emailVerified").GetBoolean());
        Assert.Empty(factory.Emails.SentTo(adminEmail));

        await HttpAssertions.AssertErrorAsync(
            await admin.PostAsJsonAsync("/users", new { name = "Otro", email = clientEmail, password = Password, role = "client" }),
            HttpStatusCode.Conflict, "email_in_use");
        await HttpAssertions.AssertErrorAsync(
            await admin.PostAsJsonAsync("/users", new { name = "Barbero", email = $"b-{Guid.NewGuid():N}@example.com", password = Password, role = "barber" }),
            HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(
            await admin.PostAsJsonAsync("/users", new { name = "Débil", email = $"d-{Guid.NewGuid():N}@example.com", password = "corta", role = "client" }),
            HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Admin_edits_clients_and_sets_a_new_password_without_mail()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var user = await factory.AddUserAsync(UserRole.Client);

        var response = await admin.PutAsJsonAsync($"/users/{user.Id}", new
        {
            name = "Nombre Corregido",
            phone = "341 444 0000",
            newPassword = "NuevaClave2026",
            role = "admin",
            email = "otro@example.com",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id));
        Assert.Equal("Nombre Corregido", stored.Name);
        Assert.Equal(UserRole.Client, stored.Role);
        Assert.Equal(user.Email, stored.Email);
        var login = await factory.CreateClient().PostAsJsonAsync("/auth/login", new { email = user.Email, password = "NuevaClave2026" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Empty(factory.Emails.SentTo(user.Email));
    }

    [Fact]
    public async Task Barbers_are_managed_from_their_own_endpoints()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (_, barber) = await factory.AddBarberAsync();

        await HttpAssertions.AssertErrorAsync(
            await admin.PutAsJsonAsync($"/users/{barber.Id}", new { name = "Cambio" }), HttpStatusCode.BadRequest, "use_barbers_endpoint");
        await HttpAssertions.AssertErrorAsync(
            await admin.PostAsync($"/users/{barber.Id}/deactivate", null), HttpStatusCode.BadRequest, "use_barbers_endpoint");
    }

    [Fact]
    public async Task Barber_sees_only_the_active_clients_that_booked_with_him()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var (_, otherBarber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var regular = await factory.AddUserAsync(UserRole.Client);
        await factory.UpdateUserAsync(regular.Id, u => u.Phone = "341 111 2222");
        var once = await factory.AddUserAsync(UserRole.Client);
        var inactive = await factory.AddUserAsync(UserRole.Client, isActive: false);
        var foreign = await factory.AddUserAsync(UserRole.Client);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 10, 10, 0), AppointmentStatus.Completed, regular.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 17, 10, 0), AppointmentStatus.Completed, regular.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 30, 10, 0), AppointmentStatus.Pending, regular.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 12, 10, 0), AppointmentStatus.Completed, once.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 13, 10, 0), AppointmentStatus.Cancelled, once.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 14, 10, 0), AppointmentStatus.Completed, inactive.Id);
        await factory.AddAppointmentAsync(otherBarber, cut, Local(2026, 11, 14, 11, 0), AppointmentStatus.Completed, foreign.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 11, 15, 10, 0), AppointmentStatus.Completed);

        var client = factory.ClientWithToken(factory.TokenFor(barber));
        var list = await client.GetFromJsonAsync<JsonElement>("/clients/mine");

        var items = list.EnumerateArray().ToList();
        Assert.Equal([regular.Id, once.Id], items.Select(i => i.GetProperty("id").GetInt32()));
        Assert.Equal("341 111 2222", items[0].GetProperty("phone").GetString());
        Assert.Equal(2, items[0].GetProperty("completedAppointments").GetInt32());
        Assert.Equal(1, items[0].GetProperty("upcomingAppointments").GetInt32());
        Assert.Equal(Local(2026, 11, 17, 10, 0), items[0].GetProperty("lastAppointmentDate").GetDateTimeOffset());
        Assert.Equal(Local(2026, 11, 30, 10, 0), items[0].GetProperty("nextAppointmentDate").GetDateTimeOffset());
        Assert.Equal(1, items[1].GetProperty("completedAppointments").GetInt32());
        Assert.False(items[0].TryGetProperty("email", out _));

        await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(UserRole.Client)).GetAsync("/clients/mine"), HttpStatusCode.Forbidden, "forbidden");
    }
}
