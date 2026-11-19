using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Appointments;

public sealed class AppointmentQueryTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    [Fact]
    public async Task Barber_only_lists_his_appointments_and_admin_can_filter()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        var (_, other) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 1, 10, 0));
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 2, 10, 0), AppointmentStatus.Cancelled);
        await factory.AddAppointmentAsync(other, cut, Local(2026, 12, 1, 10, 0));

        var mine = await factory.ClientWithToken(factory.TokenFor(barber))
            .GetFromJsonAsync<JsonElement>($"/appointments?barberId={other.Id}&pageSize=100");
        Assert.All(mine.GetProperty("items").EnumerateArray(), a => Assert.Equal(barber.Id, a.GetProperty("barber").GetProperty("id").GetInt32()));
        Assert.Equal(2, mine.GetProperty("total").GetInt32());

        var admin = await factory.ClientForAsync(UserRole.Admin);
        var filtered = await admin.GetFromJsonAsync<JsonElement>(
            $"/appointments?barberId={barber.Id}&status=pending&from=2026-12-01&to=2026-12-01&branchId={branch.Id}");
        Assert.Equal(1, filtered.GetProperty("total").GetInt32());

        var paged = await admin.GetFromJsonAsync<JsonElement>($"/appointments?barberId={barber.Id}&page=2&pageSize=1");
        Assert.Equal(2, paged.GetProperty("total").GetInt32());
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(2, paged.GetProperty("page").GetInt32());
        Assert.Equal(Local(2026, 12, 1, 10, 0), paged.GetProperty("items")[0].GetProperty("appointmentDate").GetDateTimeOffset());
    }

    [Fact]
    public async Task List_parameters_are_validated_and_clients_cannot_use_the_staff_list()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var client = await factory.ClientForAsync(UserRole.Client);

        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/appointments?pageSize=500"), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/appointments?from=2026-12-02&to=2026-12-01"), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await client.GetAsync("/appointments"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/appointments/my"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task My_appointments_hide_expired_ones_and_other_clients()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var me = await factory.AddUserAsync(UserRole.Client);
        var someone = await factory.AddUserAsync(UserRole.Client);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 3, 10, 0), clientId: me.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 3, 11, 0), AppointmentStatus.Expired, clientId: me.Id);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 3, 12, 0), clientId: someone.Id);

        var mine = await factory.ClientWithToken(factory.TokenFor(me)).GetFromJsonAsync<JsonElement>("/appointments/my");

        Assert.Equal(1, mine.GetProperty("total").GetInt32());
        Assert.Equal(me.Id, mine.GetProperty("items")[0].GetProperty("client").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Single_appointment_is_visible_to_its_client_its_barber_and_admins_only()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var (_, other) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var owner = await factory.AddUserAsync(UserRole.Client);
        var stranger = await factory.AddUserAsync(UserRole.Client);
        await factory.AddAppointmentAsync(barber, cut, Local(2026, 12, 4, 10, 0), clientId: owner.Id);
        var id = await factory.WithDbAsync(db => Task.FromResult(db.Appointments.Single(a => a.ClientId == owner.Id).Id));
        var url = $"/appointments/{id}";

        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithToken(factory.TokenFor(owner)).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.ClientWithToken(factory.TokenFor(barber)).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await factory.ClientForAsync(UserRole.Admin)).GetAsync(url)).StatusCode);
        await HttpAssertions.AssertErrorAsync(await factory.ClientWithToken(factory.TokenFor(stranger)).GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await factory.ClientWithToken(factory.TokenFor(other)).GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");
        await HttpAssertions.AssertErrorAsync(await factory.ClientWithToken(factory.TokenFor(owner)).GetAsync("/appointments/999999"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Client_search_needs_three_letters_returns_ten_and_masks_emails()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 12; i++)
            {
                db.Users.Add(new User { Name = $"Zoe {tag} {i:00}", Email = $"zoe{i}-{tag}@example.com", PasswordHash = "x", Role = UserRole.Client });
            }

            db.Users.Add(new User { Name = $"Zoe {tag} baja", Email = $"baja-{tag}@example.com", PasswordHash = "x", Role = UserRole.Client, IsActive = false });
            return await db.SaveChangesAsync();
        });
        var barber = await factory.ClientForAsync(UserRole.Barber);

        await HttpAssertions.AssertErrorAsync(await barber.GetAsync("/clients/search?q=zo"), HttpStatusCode.BadRequest, "validation_error");
        var results = await barber.GetFromJsonAsync<JsonElement>($"/clients/search?q={tag.ToUpperInvariant()}");

        Assert.Equal(10, results.GetArrayLength());
        var first = results[0];
        Assert.False(first.TryGetProperty("email", out _));
        Assert.StartsWith("zo***@", first.GetProperty("maskedEmail").GetString());
        Assert.DoesNotContain(results.EnumerateArray(), r => r.GetProperty("name").GetString()!.EndsWith("baja"));
        await HttpAssertions.AssertErrorAsync(
            await (await factory.ClientForAsync(UserRole.Client)).GetAsync($"/clients/search?q={tag}"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public void Client_search_uses_case_insensitive_like_with_escaped_wildcards_in_postgres()
    {
        using var db = TestDb.CreateNpgsqlModelOnly();

        var sql = Api.Controllers.ClientsController.Matching(db.Users, "50%_off", npgsql: true).ToQueryString();

        Assert.Contains("ILIKE", sql);
        Assert.Contains("ESCAPE", sql);
        Assert.Contains(@"%50\%\_off%", sql);
        Assert.Contains("'client'", sql);
    }

    [Theory]
    [InlineData("juan.perez@example.com", "ju***@example.com")]
    [InlineData("a@example.com", "a***@example.com")]
    [InlineData("ab@example.com", "a***@example.com")]
    public void Emails_are_masked(string email, string expected) => Assert.Equal(expected, EmailMask.Mask(email));
}
