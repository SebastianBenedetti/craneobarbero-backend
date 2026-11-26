using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Reviews;

public sealed class ReviewsTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private async Task<int> AppointmentAsync(User barber, Cut cut, DateTimeOffset start, AppointmentStatus status, int clientId)
    {
        await factory.AddAppointmentAsync(barber, cut, start, status, clientId);
        return await factory.WithDbAsync(db => db.Appointments
            .Where(a => a.BarberId == barber.Id && a.AppointmentDate == start)
            .Select(a => a.Id).SingleAsync());
    }

    [Fact]
    public async Task Client_rates_a_completed_appointment_once()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var me = await factory.AddUserAsync(UserRole.Client);
        var id = await AppointmentAsync(barber, cut, Local(2026, 10, 1, 10, 0), AppointmentStatus.Completed, me.Id);
        var client = factory.ClientWithToken(factory.TokenFor(me));

        var response = await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingBarber = 5, ratingCut = 4, comment = "  Excelente  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, review.GetProperty("appointmentId").GetInt32());
        Assert.Equal(5, review.GetProperty("ratingBarber").GetInt32());
        Assert.Equal(4, review.GetProperty("ratingCut").GetInt32());
        Assert.Equal("Excelente", review.GetProperty("comment").GetString());
        Assert.Equal(barber.Id, review.GetProperty("barber").GetProperty("id").GetInt32());

        var again = await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingBarber = 1, ratingCut = 1 });
        await HttpAssertions.AssertErrorAsync(again, HttpStatusCode.Conflict, "already_reviewed");

        var mine = await client.GetFromJsonAsync<JsonElement>("/reviews/mine");
        Assert.Single(mine.EnumerateArray());
        Assert.Equal(id, mine[0].GetProperty("appointmentId").GetInt32());
    }

    [Fact]
    public async Task Only_the_owner_of_a_completed_appointment_can_rate_it()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var owner = await factory.AddUserAsync(UserRole.Client);
        var stranger = await factory.AddUserAsync(UserRole.Client);
        var pending = await AppointmentAsync(barber, cut, Local(2026, 12, 10, 10, 0), AppointmentStatus.Pending, owner.Id);
        var cancelled = await AppointmentAsync(barber, cut, Local(2026, 10, 2, 10, 0), AppointmentStatus.Cancelled, owner.Id);
        var completed = await AppointmentAsync(barber, cut, Local(2026, 10, 3, 10, 0), AppointmentStatus.Completed, owner.Id);
        var ownerClient = factory.ClientWithToken(factory.TokenFor(owner));
        var body = new { ratingBarber = 4, ratingCut = 4 };

        await HttpAssertions.AssertErrorAsync(await ownerClient.PostAsJsonAsync($"/appointments/{pending}/review", body), HttpStatusCode.Conflict, "not_reviewable");
        await HttpAssertions.AssertErrorAsync(await ownerClient.PostAsJsonAsync($"/appointments/{cancelled}/review", body), HttpStatusCode.Conflict, "not_reviewable");
        await HttpAssertions.AssertErrorAsync(
            await factory.ClientWithToken(factory.TokenFor(stranger)).PostAsJsonAsync($"/appointments/{completed}/review", body),
            HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(
            await factory.ClientWithToken(factory.TokenFor(barber)).PostAsJsonAsync($"/appointments/{completed}/review", body),
            HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await ownerClient.PostAsJsonAsync("/appointments/999999/review", body), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Ratings_and_comment_are_validated()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var me = await factory.AddUserAsync(UserRole.Client);
        var id = await AppointmentAsync(barber, cut, Local(2026, 10, 4, 10, 0), AppointmentStatus.Completed, me.Id);
        var client = factory.ClientWithToken(factory.TokenFor(me));

        await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingBarber = 0, ratingCut = 3 }), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingBarber = 3, ratingCut = 6 }), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingCut = 3 }), HttpStatusCode.BadRequest, "validation_error");
        await HttpAssertions.AssertErrorAsync(
            await client.PostAsJsonAsync($"/appointments/{id}/review", new { ratingBarber = 3, ratingCut = 3, comment = new string('a', 501) }),
            HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Barber_sees_only_his_reviews_and_admin_sees_all_with_averages()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        var (_, other) = await factory.AddBarberAsync();
        var cut = await factory.AddCutAsync(30);
        var me = await factory.AddUserAsync(UserRole.Client);
        var client = factory.ClientWithToken(factory.TokenFor(me));
        var first = await AppointmentAsync(barber, cut, Local(2026, 9, 1, 10, 0), AppointmentStatus.Completed, me.Id);
        var second = await AppointmentAsync(barber, cut, Local(2026, 9, 2, 10, 0), AppointmentStatus.Completed, me.Id);
        var third = await AppointmentAsync(other, cut, Local(2026, 9, 3, 10, 0), AppointmentStatus.Completed, me.Id);
        await client.PostAsJsonAsync($"/appointments/{first}/review", new { ratingBarber = 5, ratingCut = 4 });
        await client.PostAsJsonAsync($"/appointments/{second}/review", new { ratingBarber = 4, ratingCut = 3 });
        await client.PostAsJsonAsync($"/appointments/{third}/review", new { ratingBarber = 2, ratingCut = 2 });

        var own = await factory.ClientWithToken(factory.TokenFor(barber)).GetFromJsonAsync<JsonElement>($"/reviews?barberId={other.Id}");
        Assert.Equal(2, own.GetProperty("total").GetInt32());
        Assert.Equal(4.5m, own.GetProperty("averageBarber").GetDecimal());
        Assert.Equal(3.5m, own.GetProperty("averageCut").GetDecimal());
        Assert.Equal(JsonValueKind.Null, own.GetProperty("byBarber").ValueKind);
        Assert.All(own.GetProperty("items").EnumerateArray(), r => Assert.Equal(barber.Id, r.GetProperty("barber").GetProperty("id").GetInt32()));
        Assert.Equal(second, own.GetProperty("items")[0].GetProperty("appointmentId").GetInt32());

        var admin = await factory.ClientForAsync(UserRole.Admin);
        var filtered = await admin.GetFromJsonAsync<JsonElement>($"/reviews?branchId={branch.Id}");
        Assert.Equal(2, filtered.GetProperty("total").GetInt32());
        var byOther = await admin.GetFromJsonAsync<JsonElement>($"/reviews?barberId={other.Id}");
        Assert.Equal(1, byOther.GetProperty("total").GetInt32());
        var summary = byOther.GetProperty("byBarber");
        Assert.Single(summary.EnumerateArray());
        Assert.Equal(2m, summary[0].GetProperty("averageBarber").GetDecimal());

        await HttpAssertions.AssertErrorAsync(await client.GetAsync("/reviews"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/reviews/mine"), HttpStatusCode.Forbidden, "forbidden");
        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/reviews?pageSize=500"), HttpStatusCode.BadRequest, "validation_error");
    }
}
