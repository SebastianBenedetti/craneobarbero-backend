using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Schedule;

public sealed record HolidaySlotChange(int SlotsRemoved, int SlotsKeptWithAppointments, int SlotsRestored);

public sealed record HolidaySyncResult(
    IReadOnlyList<int> Years,
    IReadOnlyList<int> FailedYears,
    int Added,
    int Renamed,
    int Removed,
    int SlotsRemoved,
    IReadOnlyList<DateOnly> DaysWithAppointments);

public sealed class HolidayService(
    AppDbContext db,
    OpeningSchedule opening,
    BusinessClock clock,
    SlotGenerator generator,
    IHolidaySource source,
    ILogger<HolidayService> logger)
{
    public const string ApiSource = "api";
    public const string ManualSource = "manual";
    public const string HolidayReason = "holiday";

    public async Task<HolidaySyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;
        var years = new[] { today.Year, today.Year + 1 };
        var failed = new List<int>();
        var added = 0;
        var renamed = 0;
        var removed = 0;
        var slotsRemoved = 0;
        var withAppointments = new List<DateOnly>();
        var datesToRegenerate = new List<DateOnly>();

        foreach (var year in years)
        {
            IReadOnlyList<ExternalHoliday> external;
            try
            {
                external = await source.GetNationalHolidaysAsync(year, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogWarning(exception, "No se pudieron obtener los feriados de {Year}", year);
                failed.Add(year);
                continue;
            }

            if (external.Count == 0)
            {
                failed.Add(year);
                continue;
            }

            var first = new DateOnly(year, 1, 1);
            var last = new DateOnly(year, 12, 31);
            var stored = await db.Holidays.Where(h => h.Date >= first && h.Date <= last).ToListAsync(cancellationToken);
            var byDate = stored.ToDictionary(h => h.Date);
            var externalDates = external.Select(h => h.Date).ToHashSet();

            foreach (var holiday in external)
            {
                if (byDate.TryGetValue(holiday.Date, out var current))
                {
                    if (current.Source == ApiSource && current.Name != holiday.Name)
                    {
                        current.Name = holiday.Name;
                        renamed++;
                    }

                    continue;
                }

                db.Holidays.Add(new Holiday
                {
                    Date = holiday.Date,
                    Name = holiday.Name,
                    Type = HolidayType.National,
                    Source = ApiSource,
                    IsActive = true,
                });
                added++;

                if (holiday.Date >= today)
                {
                    var change = await CloseDayAsync(holiday.Date, null, cancellationToken);
                    slotsRemoved += change.SlotsRemoved;
                    if (change.SlotsKeptWithAppointments > 0)
                    {
                        withAppointments.Add(holiday.Date);
                    }
                }
            }

            foreach (var obsolete in stored.Where(h => h.Source == ApiSource && h.Date >= today && !externalDates.Contains(h.Date)))
            {
                db.Holidays.Remove(obsolete);
                removed++;
                if (obsolete.IsActive)
                {
                    await ReopenDayAsync(obsolete.Date, cancellationToken);
                    datesToRegenerate.Add(obsolete.Date);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var date in datesToRegenerate)
        {
            await generator.GenerateAsync(from: date, to: date, cancellationToken: cancellationToken);
        }

        if (withAppointments.Count > 0)
        {
            logger.LogWarning("Hay feriados nuevos con turnos activos: {Dates}", string.Join(", ", withAppointments));
        }

        return new HolidaySyncResult(years, failed, added, renamed, removed, slotsRemoved, withAppointments);
    }

    public async Task<HolidaySlotChange> CloseDayAsync(DateOnly date, int? adminId, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = opening.DayRange(date);
        var now = clock.UtcNow;

        var slots = await db.AvailableSlots
            .Where(s => s.DeletedAt == null && s.StartAt >= dayStart && s.StartAt < dayEnd && s.StartAt > now)
            .ToListAsync(cancellationToken);
        if (slots.Count == 0)
        {
            return new HolidaySlotChange(0, 0, 0);
        }

        var appointments = await BlockingAppointmentsAsync(dayStart, dayEnd, cancellationToken);
        var removed = 0;
        var kept = 0;
        foreach (var slot in slots)
        {
            var slotEnd = slot.StartAt + opening.SlotLength;
            if (appointments.Any(a => a.BarberId == slot.BarberId && a.Start < slotEnd && a.End > slot.StartAt))
            {
                kept++;
                continue;
            }

            slot.DeletedAt = now;
            slot.DeletedById = adminId;
            slot.DeleteReason = HolidayReason;
            removed++;
        }

        return new HolidaySlotChange(removed, kept, 0);
    }

    public async Task<int> ReopenDayAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = opening.DayRange(date);
        var slots = await db.AvailableSlots
            .Where(s => s.DeletedAt != null && s.DeleteReason == HolidayReason && s.StartAt >= dayStart && s.StartAt < dayEnd)
            .ToListAsync(cancellationToken);

        foreach (var slot in slots)
        {
            slot.DeletedAt = null;
            slot.DeletedById = null;
            slot.DeleteReason = null;
        }

        return slots.Count;
    }

    public async Task<int> CountAppointmentsAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var (dayStart, dayEnd) = opening.DayRange(date);
        return (await BlockingAppointmentsAsync(dayStart, dayEnd, cancellationToken)).Count;
    }

    private async Task<List<(int BarberId, DateTimeOffset Start, DateTimeOffset End)>> BlockingAppointmentsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => OpeningSchedule.BlockingStatuses.Contains(a.Status) && a.AppointmentDate < to && a.EndDate > from)
            .Select(a => new { a.BarberId, a.AppointmentDate, a.EndDate })
            .ToListAsync(cancellationToken);
        return rows.Select(a => (a.BarberId, a.AppointmentDate, a.EndDate)).ToList();
    }
}
