using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class ScheduleManagementTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Tuesday = new(2026, 11, 24);
    private static readonly DateOnly Wednesday = new(2026, 11, 25);

    [Fact]
    public async Task Remove_preview_lists_affected_appointments_and_refund_total()
    {
        var s = await SetupAsync();
        await ManualAsync(s.Barber, Local(2026, 11, 24, 10, 0));
        await OnlinePaidAsync(s.Barber, Local(2026, 11, 24, 11, 0));

        var preview = await s.Admin.PostAsJsonAsync("/schedule/slots/remove/preview", DayOf(s.Branch.Id, Tuesday));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var json = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(36, json.GetProperty("slotsToRemove").GetInt32());
        Assert.Equal(2, json.GetProperty("slotsWithAppointments").GetInt32());
        Assert.Equal(2, json.GetProperty("affectedCount").GetInt32());
        Assert.Equal(1000m, json.GetProperty("totalRefund").GetDecimal());
        var first = json.GetProperty("affectedAppointments")[0];
        Assert.Equal("10:00", first.GetProperty("time").GetString());
        Assert.False(first.GetProperty("refundable").GetBoolean());
    }

    [Fact]
    public async Task Remove_keeping_appointments_only_removes_free_slots()
    {
        var s = await SetupAsync();
        var manual = await ManualAsync(s.Barber, Local(2026, 11, 24, 10, 0));

        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/remove", Remove(s.Branch.Id, Tuesday, cancel: false)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(35, result.GetProperty("removed").GetInt32());
        Assert.Equal(1, result.GetProperty("keptWithAppointments").GetInt32());
        Assert.Equal(0, result.GetProperty("cancelledAppointments").GetInt32());
        var appointment = await LoadAsync(manual);
        Assert.Equal(AppointmentStatus.Pending, appointment.Status);
        var slots = await factory.SlotsAsync(s.Barber.Id, Tuesday);
        Assert.Null(slots.Single(x => x.StartAt == Local(2026, 11, 24, 10, 0)).DeletedAt);
        var removed = slots.First(x => x.StartAt == Local(2026, 11, 24, 9, 0));
        Assert.Equal("Cierre por reforma", removed.DeleteReason);
        Assert.NotNull(removed.DeletedById);
    }

    [Fact]
    public async Task Remove_cancelling_appointments_refunds_online_payments_even_with_little_time()
    {
        var s = await SetupAsync();
        await factory.SetSlotsAsync(s.Barber.Id, [Local(2026, 11, 23, 9, 0)], SlotOrigin.Auto);
        var soon = await OnlinePaidAsync(s.Barber, Local(2026, 11, 23, 9, 0));
        var manual = await ManualAsync(s.Barber, Local(2026, 11, 24, 10, 0));
        var body = new
        {
            branchIds = new[] { s.Branch.Id }, dateFrom = "2026-11-23", dateTo = "2026-11-24",
            reason = "Cierre por reforma", cancelAffected = true, expectedCancellations = 1,
        };

        var outdated = await s.Admin.PostAsJsonAsync("/schedule/slots/remove", body);
        await HttpAssertions.AssertErrorAsync(outdated, HttpStatusCode.Conflict, "preview_outdated");

        var response = await s.Admin.PostAsJsonAsync("/schedule/slots/remove", body with { expectedCancellations = 2 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("cancelledAppointments").GetInt32());
        Assert.Equal(1, result.GetProperty("refunded").GetInt32());
        Assert.Equal(1000m, result.GetProperty("totalRefunded").GetDecimal());
        var online = await LoadAsync(soon);
        Assert.Equal(AppointmentStatus.Cancelled, online.Status);
        Assert.Equal(PaymentStatus.Refunded, online.PaymentStatus);
        Assert.Equal("schedule_removed", online.CancellationReason);
        Assert.Equal(UserRole.Admin, online.CancelledByRole);
        Assert.Equal(PaymentStatus.Unpaid, (await LoadAsync(manual)).PaymentStatus);
        Assert.All(await factory.SlotsAsync(s.Barber.Id, Tuesday), x => Assert.NotNull(x.DeletedAt));
    }

    [Fact]
    public async Task Removing_one_barber_with_time_range_and_weekdays_leaves_the_rest()
    {
        var s = await SetupAsync();
        await factory.GenerateAsync(s.Other.Id, Tuesday, Wednesday);
        await factory.GenerateAsync(s.Barber.Id, Wednesday, Wednesday);

        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/remove", new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id },
            dateFrom = "2026-11-24", dateTo = "2026-11-25", timeFrom = "15:00", timeTo = "17:00",
            weekdays = new[] { "wednesday" }, reason = "Día libre", cancelAffected = false,
        })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(4, result.GetProperty("removed").GetInt32());
        Assert.All(await factory.SlotsAsync(s.Barber.Id, Tuesday), x => Assert.Null(x.DeletedAt));
        Assert.All(await factory.SlotsAsync(s.Other.Id, Wednesday), x => Assert.Null(x.DeletedAt));
        var removed = (await factory.SlotsAsync(s.Barber.Id, Wednesday)).Where(x => x.DeletedAt != null).Select(x => x.StartAt).ToList();
        Assert.Equal([Local(2026, 11, 25, 15, 0), Local(2026, 11, 25, 15, 30), Local(2026, 11, 25, 16, 0), Local(2026, 11, 25, 16, 30)], removed);
    }

    [Fact]
    public async Task Adding_a_sunday_from_10_to_14_creates_eight_bookable_manual_slots()
    {
        var s = await SetupAsync();
        var body = new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id },
            dateFrom = "2026-11-29", dateTo = "2026-11-29", timeFrom = "10:00", timeTo = "14:00",
        };

        var preview = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", body)).Content.ReadFromJsonAsync<JsonElement>();
        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add", body)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(8, preview.GetProperty("toCreate").GetInt32());
        Assert.Equal(8, result.GetProperty("created").GetInt32());
        var slots = await factory.SlotsAsync(s.Barber.Id, new DateOnly(2026, 11, 29));
        Assert.All(slots, x => Assert.Equal(SlotOrigin.Manual, x.Origin));
        var times = await factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/appointments/availability?barberId={s.Barber.Id}&cutId={s.Cut.Id}&date=2026-11-29");
        Assert.Equal(8, times.GetProperty("times").GetArrayLength());
    }

    [Fact]
    public async Task Adding_never_overwrites_open_slots_or_their_appointments_and_restores_removed_ones()
    {
        var s = await SetupAsync();
        var booked = await ManualAsync(s.Barber, Local(2026, 11, 24, 12, 0));
        await s.Admin.PostAsJsonAsync("/schedule/slots/remove", new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24",
            timeFrom = "09:00", timeTo = "11:00", reason = "Trámite", cancelAffected = false,
        });
        var body = new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id },
            dateFrom = "2026-11-24", dateTo = "2026-11-24", timeFrom = "08:00", timeTo = "13:00",
        };

        var preview = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", body)).Content.ReadFromJsonAsync<JsonElement>();
        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add", body)).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add", body)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, preview.GetProperty("toCreate").GetInt32());
        Assert.Equal(4, preview.GetProperty("toRestore").GetInt32());
        Assert.Equal(4, preview.GetProperty("toSkip").GetInt32());
        Assert.Equal(2, result.GetProperty("created").GetInt32());
        Assert.Equal(4, result.GetProperty("restored").GetInt32());
        Assert.Equal(4, result.GetProperty("skipped").GetInt32());
        Assert.Equal(10, again.GetProperty("skipped").GetInt32());
        Assert.Equal(0, again.GetProperty("created").GetInt32() + again.GetProperty("restored").GetInt32());
        var slots = await factory.SlotsAsync(s.Barber.Id, Tuesday);
        Assert.Equal(SlotOrigin.Auto, slots.Single(x => x.StartAt == Local(2026, 11, 24, 12, 0)).Origin);
        Assert.Equal(AppointmentStatus.Pending, (await LoadAsync(booked)).Status);
    }

    [Fact]
    public async Task Restoring_slots_does_not_restore_cancelled_appointments()
    {
        var s = await SetupAsync();
        var manual = await ManualAsync(s.Barber, Local(2026, 11, 24, 16, 0));
        var range = new { branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24", timeFrom = "16:00", timeTo = "16:30" };
        await s.Admin.PostAsJsonAsync("/schedule/slots/remove", new
        {
            range.branchIds, range.barberIds, range.dateFrom, range.dateTo, range.timeFrom, range.timeTo,
            reason = "Corte de luz", cancelAffected = true, expectedCancellations = 1,
        });

        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add", range)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, result.GetProperty("restored").GetInt32());
        Assert.Equal(AppointmentStatus.Cancelled, (await LoadAsync(manual)).Status);
        var slot = (await factory.SlotsAsync(s.Barber.Id, Tuesday)).Single(x => x.StartAt == Local(2026, 11, 24, 16, 0));
        Assert.Null(slot.DeletedAt);
        Assert.Equal(SlotOrigin.Manual, slot.Origin);
    }

    [Fact]
    public async Task All_branches_option_reaches_every_active_branch()
    {
        var s = await SetupAsync();
        var (_, elsewhere) = await factory.AddBarberAsync();

        var result = await (await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", new
        {
            allBranches = true, dateFrom = "2026-11-29", dateTo = "2026-11-29", timeFrom = "10:00", timeTo = "11:00",
        })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("branches").GetInt32() >= 2);
        Assert.True(result.GetProperty("toCreate").GetInt32() >= 2 * 3);
        Assert.NotNull(elsewhere);
    }

    [Fact]
    public async Task Invalid_requests_and_non_admins_are_rejected()
    {
        var s = await SetupAsync();
        var (_, foreign) = await factory.AddBarberAsync();

        var past = await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", new { branchIds = new[] { s.Branch.Id }, dateFrom = "2026-11-20", dateTo = "2026-11-21" });
        var noBranch = await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", new { dateFrom = "2026-11-24", dateTo = "2026-11-24" });
        var offGrid = await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", new { branchIds = new[] { s.Branch.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24", timeFrom = "10:15", timeTo = "11:00" });
        var noReason = await s.Admin.PostAsJsonAsync("/schedule/slots/remove", new { branchIds = new[] { s.Branch.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24", cancelAffected = false });
        var outsider = await s.Admin.PostAsJsonAsync("/schedule/slots/add/preview", new { branchIds = new[] { s.Branch.Id }, barberIds = new[] { foreign.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24" });

        Assert.True((await HttpAssertions.AssertErrorAsync(past, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("dateFrom", out _));
        Assert.True((await HttpAssertions.AssertErrorAsync(noBranch, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("branchIds", out _));
        Assert.True((await HttpAssertions.AssertErrorAsync(offGrid, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("timeFrom", out _));
        Assert.True((await HttpAssertions.AssertErrorAsync(noReason, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors").TryGetProperty("reason", out _));
        await HttpAssertions.AssertErrorAsync(outsider, HttpStatusCode.BadRequest, "barber_unavailable");

        foreach (var role in new[] { UserRole.Barber, UserRole.Client })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/schedule/slots/remove", new { }), HttpStatusCode.Forbidden, "forbidden");
            await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/schedule/slots/add", new { }), HttpStatusCode.Forbidden, "forbidden");
            await HttpAssertions.AssertErrorAsync(await client.GetAsync("/schedule/overview?from=2026-11-24&to=2026-11-24"), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Overview_and_day_detail_show_the_state_of_each_slot()
    {
        var s = await SetupAsync();
        var booked = await ManualAsync(s.Barber, Local(2026, 11, 24, 10, 0));
        await s.Admin.PostAsJsonAsync("/schedule/slots/remove", new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24",
            timeFrom = "09:00", timeTo = "09:30", reason = "Llega tarde", cancelAffected = false,
        });
        await s.Admin.PostAsJsonAsync("/schedule/slots/add", new
        {
            branchIds = new[] { s.Branch.Id }, barberIds = new[] { s.Barber.Id }, dateFrom = "2026-11-24", dateTo = "2026-11-24",
            timeFrom = "20:00", timeTo = "21:00",
        });

        var overview = await s.Admin.GetFromJsonAsync<JsonElement>($"/schedule/overview?from=2026-11-24&to=2026-11-29&branchIds={s.Branch.Id}");
        var day = await s.Admin.GetFromJsonAsync<JsonElement>($"/schedule/day?date=2026-11-24&barberId={s.Barber.Id}");

        var tuesday = overview[0];
        Assert.Equal(6, overview.GetArrayLength());
        Assert.Equal(1, tuesday.GetProperty("busy").GetInt32());
        Assert.Equal(1, tuesday.GetProperty("deleted").GetInt32());
        Assert.Equal(2, tuesday.GetProperty("extra").GetInt32());
        Assert.Equal(18 + 18 + 2 - 1 - 1, tuesday.GetProperty("free").GetInt32());
        Assert.True(overview[5].GetProperty("closedByDefault").GetBoolean());

        var slots = day.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(20, slots.Count);
        var deleted = slots.Single(x => x.GetProperty("time").GetString() == "09:00");
        Assert.Equal("deleted", deleted.GetProperty("state").GetString());
        Assert.Equal("Llega tarde", deleted.GetProperty("deleteReason").GetString());
        var busy = slots.Single(x => x.GetProperty("time").GetString() == "10:00");
        Assert.Equal("busy", busy.GetProperty("state").GetString());
        Assert.Equal(booked, busy.GetProperty("appointmentId").GetInt32());
        Assert.Equal("manual", slots.Last().GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Changing_a_barbers_branch_cancels_future_appointments_after_confirmation()
    {
        var s = await SetupAsync();
        var (newBranch, _) = await factory.AddBarberAsync();
        var online = await OnlinePaidAsync(s.Barber, Local(2026, 11, 24, 9, 0));
        var manual = await ManualAsync(s.Barber, Local(2026, 11, 24, 10, 0));
        var awaiting = await AddAsync(s.Barber, Local(2026, 11, 24, 11, 0), a =>
        {
            a.Status = AppointmentStatus.AwaitingPayment;
            a.PaymentExpiresAt = Now.AddMinutes(15);
        });
        var past = await AddAsync(s.Barber, Local(2026, 11, 20, 10, 0), a => a.Status = AppointmentStatus.Completed);

        var preview = await s.Admin.GetFromJsonAsync<JsonElement>($"/barbers/{s.Barber.Id}/branch-change-preview?branchId={newBranch.Id}");
        Assert.Equal(3, preview.GetProperty("affectedCount").GetInt32());
        Assert.Equal(1000m, preview.GetProperty("totalRefund").GetDecimal());

        var unconfirmed = await s.Admin.PutAsJsonAsync($"/barbers/{s.Barber.Id}", new { name = s.Barber.Name, branchId = newBranch.Id });
        await HttpAssertions.AssertErrorAsync(unconfirmed, HttpStatusCode.Conflict, "has_future_appointments");

        var confirmed = await s.Admin.PutAsJsonAsync($"/barbers/{s.Barber.Id}", new { name = s.Barber.Name, branchId = newBranch.Id, confirm = true });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(newBranch.Id, (await confirmed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("branchId").GetInt32());
        var onlineRow = await LoadAsync(online);
        Assert.Equal(AppointmentStatus.Cancelled, onlineRow.Status);
        Assert.Equal(PaymentStatus.Refunded, onlineRow.PaymentStatus);
        Assert.Equal("barber_branch_change", onlineRow.CancellationReason);
        Assert.Equal(PaymentStatus.Unpaid, (await LoadAsync(manual)).PaymentStatus);
        var awaitingRow = await LoadAsync(awaiting);
        Assert.Equal(AppointmentStatus.Cancelled, awaitingRow.Status);
        Assert.Equal(PaymentStatus.Unpaid, awaitingRow.PaymentStatus);
        Assert.Equal(AppointmentStatus.Completed, (await LoadAsync(past)).Status);
        Assert.Equal(s.Branch.Id, (await LoadAsync(past)).BranchId);
        Assert.Equal(18, (await factory.SlotsAsync(s.Barber.Id, Tuesday)).Count(x => x.DeletedAt == null));
    }

    private sealed record Setup(Branch Branch, User Barber, User Other, Cut Cut, HttpClient Admin);

    private async Task<Setup> SetupAsync()
    {
        var (branch, barber) = await factory.AddBarberAsync();
        var other = await factory.AddBarberInBranchAsync(branch.Id);
        await factory.GenerateAsync(barber.Id, Tuesday, Tuesday);
        await factory.GenerateAsync(other.Id, Tuesday, Tuesday);
        var cut = await factory.AddCutAsync(30);
        return new Setup(branch, barber, other, cut, await factory.ClientForAsync(UserRole.Admin));
    }

    private static object DayOf(int branchId, DateOnly date) =>
        new { branchIds = new[] { branchId }, dateFrom = date.ToString("yyyy-MM-dd"), dateTo = date.ToString("yyyy-MM-dd") };

    private static object Remove(int branchId, DateOnly date, bool cancel) => new
    {
        branchIds = new[] { branchId }, dateFrom = date.ToString("yyyy-MM-dd"), dateTo = date.ToString("yyyy-MM-dd"),
        reason = "Cierre por reforma", cancelAffected = cancel,
    };

    private Task<int> AddAsync(User barber, DateTimeOffset start, Action<Appointment>? change = null) =>
        factory.WithDbAsync(async db =>
        {
            var appointment = new Appointment
            {
                AppointmentDate = start,
                EndDate = start.AddMinutes(30),
                Status = AppointmentStatus.Pending,
                ClientName = "Cliente",
                BarberId = barber.Id,
                CutId = 1,
                BranchId = barber.BranchId!.Value,
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

    private Task<int> ManualAsync(User barber, DateTimeOffset start) => AddAsync(barber, start, a =>
    {
        a.CreatedByRole = UserRole.Barber;
        a.PaymentMethod = PaymentMethod.Cash;
    });

    private async Task<int> OnlinePaidAsync(User barber, DateTimeOffset start)
    {
        var id = await AddAsync(barber, start, a => { a.PaymentStatus = PaymentStatus.Paid; a.PaidAt = Now.AddDays(-1); });
        await factory.WithDbAsync(async db =>
        {
            db.Payments.Add(new Payment { AppointmentId = id, Amount = 1000m, Method = PaymentMethod.MercadoPago, Status = TransactionStatus.Approved });
            return await db.SaveChangesAsync();
        });
        return id;
    }

    private Task<Appointment> LoadAsync(int id) =>
        factory.WithDbAsync(db => db.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
}
