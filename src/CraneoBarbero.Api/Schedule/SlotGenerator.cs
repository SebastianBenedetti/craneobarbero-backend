using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed record SlotGenerationResult(int Barbers, int Created);

public sealed class SlotGenerator(
    AppDbContext db,
    OpeningSchedule opening,
    BusinessClock clock,
    IOptions<ScheduleSettings> settings,
    ILogger<SlotGenerator> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<SlotGenerationResult> GenerateAsync(
        int? barberId = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await GenerateCoreAsync(barberId, from, to, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<int> DeleteOldPastSlotsAsync(CancellationToken cancellationToken = default)
    {
        var limit = clock.UtcNow.AddDays(-settings.Value.PastSlotRetentionDays);
        var old = db.AvailableSlots.Where(s => s.StartAt < limit);

        if (db.Database.IsRelational())
        {
            return await old.ExecuteDeleteAsync(cancellationToken);
        }

        var rows = await old.ToListAsync(cancellationToken);
        db.AvailableSlots.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private async Task<SlotGenerationResult> GenerateCoreAsync(
        int? barberId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var today = clock.Today;
        var horizon = opening.GenerationHorizon(today);
        var firstDay = from is { } f && f > today ? f : today;
        var lastDay = to is { } t && t < horizon ? t : horizon;
        if (lastDay < firstDay)
        {
            return new SlotGenerationResult(0, 0);
        }

        var barberIds = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.IsActive && u.BranchId != null)
            .Where(u => barberId == null || u.Id == barberId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        if (barberIds.Count == 0)
        {
            return new SlotGenerationResult(0, 0);
        }

        var holidays = (await db.Holidays.AsNoTracking()
                .Where(h => h.IsActive && h.Date >= firstDay && h.Date <= lastDay)
                .Select(h => h.Date)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var candidates = new List<DateTimeOffset>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (holidays.Contains(day))
            {
                continue;
            }

            candidates.AddRange(opening.BaseSlotStarts(day).Where(start => start > now));
        }

        if (candidates.Count == 0)
        {
            return new SlotGenerationResult(barberIds.Count, 0);
        }

        var rangeStart = candidates[0];
        var rangeEnd = candidates[^1];
        var created = 0;

        foreach (var id in barberIds)
        {
            var existing = (await db.AvailableSlots.AsNoTracking()
                    .Where(s => s.BarberId == id && s.StartAt >= rangeStart && s.StartAt <= rangeEnd)
                    .Select(s => s.StartAt)
                    .ToListAsync(cancellationToken))
                .Select(start => start.UtcTicks)
                .ToHashSet();

            var missing = candidates.Where(start => !existing.Contains(start.UtcTicks))
                .Select(start => new AvailableSlot { BarberId = id, StartAt = start, Origin = SlotOrigin.Auto })
                .ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            db.AvailableSlots.AddRange(missing);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                created += missing.Count;
            }
            catch (DbUpdateException exception)
            {
                logger.LogWarning(exception, "No se pudieron generar los horarios del barbero {BarberId}; se reintenta en la próxima pasada", id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return new SlotGenerationResult(barberIds.Count, created);
    }
}
