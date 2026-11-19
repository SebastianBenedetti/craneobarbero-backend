using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed record AvailabilityQuery(
    int BarberId,
    int DurationMinutes,
    DateOnly Date,
    bool ApplyBookingWindow,
    int? ClientId = null,
    int? IgnoreAppointmentId = null);

public sealed record AvailabilityDayCount(DateOnly Date, int Available, bool HasOpenSlots);

public sealed class AvailabilityService(
    AppDbContext db,
    OpeningSchedule opening,
    BusinessClock clock,
    IOptions<BusinessSettings> business)
{
    public async Task<IReadOnlyList<DateTimeOffset>> GetAvailableStartsAsync(
        AvailabilityQuery query, CancellationToken cancellationToken = default)
    {
        var (starts, _) = await ComputeAsync(query, query.Date, query.Date, cancellationToken);
        return starts;
    }

    public async Task<IReadOnlyList<AvailabilityDayCount>> CountByDayAsync(
        AvailabilityQuery query, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var (starts, open) = await ComputeAsync(query, from, to, cancellationToken);
        var available = starts.GroupBy(opening.DateOf).ToDictionary(g => g.Key, g => g.Count());
        var withOpenSlots = open.Select(ticks => opening.DateOf(new DateTimeOffset(ticks, TimeSpan.Zero))).ToHashSet();

        var result = new List<AvailabilityDayCount>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            result.Add(new AvailabilityDayCount(date, available.GetValueOrDefault(date), withOpenSlots.Contains(date)));
        }

        return result;
    }

    private async Task<(List<DateTimeOffset> Starts, HashSet<long> Open)> ComputeAsync(
        AvailabilityQuery query, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rangeStart = opening.DayRange(from).Start;
        var rangeEnd = opening.DayRange(to).End;
        var windowEnd = query.ApplyBookingWindow ? now.AddDays(business.Value.BookingWindowDays) : DateTimeOffset.MaxValue;
        if (to < from || rangeEnd <= now || rangeStart > windowEnd || query.DurationMinutes <= 0)
        {
            return ([], []);
        }

        var blocks = opening.BlocksFor(query.DurationMinutes);
        var slotLength = opening.SlotLength;
        var duration = TimeSpan.FromMinutes(query.DurationMinutes);
        var searchEnd = rangeEnd + slotLength * blocks;

        var open = (await db.AvailableSlots.AsNoTracking()
                .Where(s => s.BarberId == query.BarberId && s.DeletedAt == null && s.StartAt >= rangeStart && s.StartAt < searchEnd)
                .Select(s => s.StartAt)
                .ToListAsync(cancellationToken))
            .Select(start => start.UtcTicks)
            .ToHashSet();
        if (open.Count == 0)
        {
            return ([], open);
        }

        var busy = await BusyRangesAsync(query, rangeStart, searchEnd, cancellationToken);

        var result = new List<DateTimeOffset>();
        foreach (var ticks in open.Where(t => t >= rangeStart.UtcTicks && t < rangeEnd.UtcTicks).Order())
        {
            var start = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (start <= now || start > windowEnd || !IsOnGrid(start))
            {
                continue;
            }

            var allBlocksOpen = Enumerable.Range(0, blocks).All(i => open.Contains((start + slotLength * i).UtcTicks));
            if (!allBlocksOpen)
            {
                continue;
            }

            var end = start + duration;
            if (busy.Any(range => range.Start < end && range.End > start))
            {
                continue;
            }

            result.Add(start);
        }

        open.RemoveWhere(t => t < rangeStart.UtcTicks || t >= rangeEnd.UtcTicks || t <= now.UtcTicks);
        return (result, open);
    }

    public async Task<bool> IsAvailableAsync(AvailabilityQuery query, DateTimeOffset start, CancellationToken cancellationToken = default)
    {
        if (opening.DateOf(start) != query.Date)
        {
            return false;
        }

        var starts = await GetAvailableStartsAsync(query, cancellationToken);
        return starts.Contains(start);
    }

    private bool IsOnGrid(DateTimeOffset start)
    {
        var time = opening.TimeOf(start);
        return time.Second == 0 && time.Millisecond == 0 && time.Minute % opening.SlotMinutes == 0;
    }

    private async Task<List<(DateTimeOffset Start, DateTimeOffset End)>> BusyRangesAsync(
        AvailabilityQuery query, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => OpeningSchedule.BlockingStatuses.Contains(a.Status) && a.AppointmentDate < to && a.EndDate > from)
            .Where(a => a.BarberId == query.BarberId || (query.ClientId != null && a.ClientId == query.ClientId))
            .Where(a => query.IgnoreAppointmentId == null || a.Id != query.IgnoreAppointmentId)
            .Select(a => new { a.AppointmentDate, a.EndDate })
            .ToListAsync(cancellationToken);
        return rows.Select(a => (a.AppointmentDate, a.EndDate)).ToList();
    }
}
