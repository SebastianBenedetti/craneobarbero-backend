using System.Net;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Schedule;

public sealed class SlotGeneratorTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Monday = new(2026, 11, 23);
    private static readonly DateOnly Saturday = new(2026, 11, 28);
    private static readonly DateOnly Sunday = new(2026, 11, 29);

    [Fact]
    public async Task Generates_the_base_opening_hours_every_30_minutes()
    {
        var (_, barber) = await factory.AddBarberAsync();

        await factory.GenerateAsync(barber.Id, Monday, Sunday);

        var monday = await factory.SlotsAsync(barber.Id, Monday);
        var times = monday.Select(s => s.StartAt.ToOffset(TimeSpan.FromHours(-3)).ToString("HH:mm")).ToList();
        Assert.Equal(18, monday.Count);
        Assert.Equal("09:00", times.First());
        Assert.Contains("12:30", times);
        Assert.DoesNotContain("13:00", times);
        Assert.Contains("15:00", times);
        Assert.Equal("19:30", times.Last());
        Assert.All(monday, s => Assert.Equal(SlotOrigin.Auto, s.Origin));

        var saturday = await factory.SlotsAsync(barber.Id, Saturday);
        Assert.Equal(12, saturday.Count);
        Assert.Equal(Local(2026, 11, 28, 14, 30), saturday.Last().StartAt);
        Assert.Empty(await factory.SlotsAsync(barber.Id, Sunday));
    }

    [Fact]
    public async Task Generates_twelve_months_ahead_and_never_in_the_past()
    {
        var (_, barber) = await factory.AddBarberAsync();
        factory.Time.SetUtcNow(Local(2026, 11, 23, 10, 15));
        try
        {
            await factory.GenerateAsync(barber.Id);
        }
        finally
        {
            factory.Time.SetUtcNow(Now);
        }

        var monday = await factory.SlotsAsync(barber.Id, Monday);
        Assert.Equal(Local(2026, 11, 23, 10, 30), monday.First().StartAt);
        var last = await factory.WithDbAsync(db => db.AvailableSlots.Where(s => s.BarberId == barber.Id).MaxAsync(s => s.StartAt));
        Assert.Equal(new DateOnly(2027, 11, 23), DateOnly.FromDateTime(last.ToOffset(TimeSpan.FromHours(-3)).DateTime));
    }

    [Fact]
    public async Task Running_twice_does_not_duplicate_and_removed_slots_are_not_recreated()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var first = await factory.GenerateAsync(barber.Id, Monday, Sunday);
        var removedAt = Local(2026, 11, 24, 10, 0);
        await factory.WithDbAsync(async db =>
        {
            var slot = await db.AvailableSlots.SingleAsync(s => s.BarberId == barber.Id && s.StartAt == removedAt);
            slot.DeletedAt = Now;
            slot.DeleteReason = "Capacitación";
            return await db.SaveChangesAsync();
        });

        var second = await factory.GenerateAsync(barber.Id, Monday, Sunday);

        Assert.Equal(5 * 18 + 12 - 0, first.Created);
        Assert.Equal(0, second.Created);
        var tuesday = await factory.SlotsAsync(barber.Id, new DateOnly(2026, 11, 24));
        Assert.Equal(18, tuesday.Count);
        Assert.NotNull(tuesday.Single(s => s.StartAt == removedAt).DeletedAt);
    }

    [Fact]
    public async Task Skips_holidays_and_inactive_barbers()
    {
        var (_, barber) = await factory.AddBarberAsync();
        var (_, inactive) = await factory.AddBarberAsync(isActive: false);
        var holiday = new DateOnly(2026, 12, 8);
        await factory.WithDbAsync(async db =>
        {
            db.Holidays.Add(new Holiday { Date = holiday, Name = "Inmaculada Concepción", Type = HolidayType.National, Source = "api" });
            return await db.SaveChangesAsync();
        });

        await factory.GenerateAsync(null, holiday.AddDays(-1), holiday.AddDays(1));

        Assert.Empty(await factory.SlotsAsync(barber.Id, holiday));
        Assert.Equal(18, (await factory.SlotsAsync(barber.Id, holiday.AddDays(-1))).Count);
        Assert.Empty(await factory.SlotsAsync(inactive.Id, holiday.AddDays(-1)));
    }

    [Fact]
    public async Task Reactivating_a_barber_generates_missing_slots_but_keeps_removed_ones()
    {
        var (_, barber) = await factory.AddBarberAsync(isActive: false);
        var removedAt = Local(2026, 11, 25, 9, 0);
        await factory.WithDbAsync(async db =>
        {
            db.AvailableSlots.Add(new AvailableSlot { BarberId = barber.Id, StartAt = removedAt, DeletedAt = Now, DeleteReason = "Vacaciones" });
            return await db.SaveChangesAsync();
        });
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.PostAsync($"/barbers/{barber.Id}/activate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wednesday = await factory.SlotsAsync(barber.Id, new DateOnly(2026, 11, 25));
        Assert.Equal(18, wednesday.Count);
        Assert.NotNull(wednesday.Single(s => s.StartAt == removedAt).DeletedAt);
        var total = await factory.WithDbAsync(db => db.AvailableSlots.CountAsync(s => s.BarberId == barber.Id));
        Assert.True(total > 5000);
    }

    [Fact]
    public async Task Old_past_slots_are_deleted_but_future_ones_are_kept()
    {
        var (_, barber) = await factory.AddBarberAsync();
        await factory.SetSlotsAsync(barber.Id, [Now.AddDays(-91), Now.AddDays(-30), Now.AddDays(5)]);

        using var scope = factory.Services.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<SlotGenerator>().DeleteOldPastSlotsAsync();

        Assert.True(deleted >= 1);
        var remaining = await factory.WithDbAsync(db => db.AvailableSlots.Where(s => s.BarberId == barber.Id).CountAsync());
        Assert.Equal(2, remaining);
    }
}
