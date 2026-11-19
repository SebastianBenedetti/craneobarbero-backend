using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed class ScheduleManagement(
    AppDbContext db,
    OpeningSchedule opening,
    BusinessClock clock,
    AppointmentChanges changes,
    AppointmentNotifications notifications,
    IOptions<BusinessSettings> business)
{
    private static readonly AppointmentStatus[] CancellableStatuses = [AppointmentStatus.Pending, AppointmentStatus.AwaitingPayment];

    private sealed record Selection(IReadOnlyList<int> BranchIds, IReadOnlyList<int> BarberIds, SlotRangeRequest Range);

    public async Task<RemoveSlotsPreview> PreviewRemovalAsync(SlotRangeRequest request, CancellationToken cancellationToken)
    {
        var selection = await ResolveAsync(request, cancellationToken);
        var (slots, affected) = await LoadRemovalAsync(selection, cancellationToken);
        var details = await DescribeAsync(affected, cancellationToken);
        var withAppointments = slots.Count(slot => affected.Any(a => Covers(a, slot)));

        return new RemoveSlotsPreview(
            slots.Count,
            withAppointments,
            selection.BranchIds.Count,
            selection.BarberIds.Count,
            details.Count,
            details.Where(d => d.Refundable).Sum(d => d.Price),
            details);
    }

    public async Task<RemoveSlotsResult> RemoveAsync(RemoveSlotsRequest request, int adminId, CancellationToken cancellationToken)
    {
        var selection = await ResolveAsync(request, cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);

        var (slots, affected) = await LoadRemovalAsync(selection, cancellationToken, tracking: true);
        var cancelAffected = request.CancelAffected == true;
        if (cancelAffected && request.ExpectedCancellations != affected.Count)
        {
            throw new ConflictException(
                $"Los turnos afectados cambiaron desde la vista previa (ahora son {affected.Count}). Revisá de nuevo antes de confirmar.",
                ErrorCodes.PreviewOutdated);
        }

        var now = clock.UtcNow;
        var reason = request.Reason!.Trim();
        var actor = new Actor(adminId, UserRole.Admin);
        var refunded = 0;
        var totalRefunded = 0m;

        var toNotify = new List<int>();
        if (cancelAffected)
        {
            foreach (var appointment in affected)
            {
                if (appointment.Status == AppointmentStatus.Pending)
                {
                    toNotify.Add(appointment.Id);
                }

                if (await changes.CancelForBusinessReasonAsync(appointment, actor, ScheduleLimits.ScheduleRemovedReason, cancellationToken))
                {
                    refunded++;
                    totalRefunded += appointment.Price;
                }
            }
        }

        var removed = 0;
        var kept = 0;
        foreach (var slot in slots)
        {
            if (!cancelAffected && affected.Any(a => Covers(a, slot)))
            {
                kept++;
                continue;
            }

            slot.DeletedAt = now;
            slot.DeletedById = adminId;
            slot.DeleteReason = reason;
            removed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await notifications.SendCancellationsAsync(toNotify, reason, cancellationToken);
        return new RemoveSlotsResult(removed, kept, cancelAffected ? affected.Count : 0, refunded, totalRefunded);
    }

    public async Task<AddSlotsPreview> PreviewAdditionAsync(SlotRangeRequest request, CancellationToken cancellationToken)
    {
        var selection = await ResolveAsync(request, cancellationToken);
        var (create, restore, skip) = await ClassifyAdditionAsync(selection, cancellationToken, tracking: false);
        return new AddSlotsPreview(create.Count, restore.Count, skip, selection.BranchIds.Count, selection.BarberIds.Count);
    }

    public async Task<AddSlotsResult> AddAsync(SlotRangeRequest request, int adminId, CancellationToken cancellationToken)
    {
        var selection = await ResolveAsync(request, cancellationToken);
        await using var transaction = await BeginAsync(cancellationToken);

        var (create, restore, skip) = await ClassifyAdditionAsync(selection, cancellationToken, tracking: true);
        foreach (var slot in restore)
        {
            slot.DeletedAt = null;
            slot.DeletedById = null;
            slot.DeleteReason = null;
            slot.Origin = SlotOrigin.Manual;
        }

        db.AvailableSlots.AddRange(create.Select(c => new AvailableSlot
        {
            BarberId = c.BarberId,
            StartAt = c.Start,
            Origin = SlotOrigin.Manual,
            CreatedById = adminId,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("La agenda cambió mientras se agregaban los horarios. Probá de nuevo.", ErrorCodes.PreviewOutdated);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new AddSlotsResult(create.Count, restore.Count, skip);
    }

    public async Task<IReadOnlyList<ScheduleDay>> OverviewAsync(
        DateOnly from, DateOnly to, int[]? branchIds, int[]? barberIds, CancellationToken cancellationToken)
    {
        if (to < from || to.DayNumber - from.DayNumber >= ScheduleLimits.MaxOverviewDays)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["to"] = [$"El rango tiene que ser de 1 a {ScheduleLimits.MaxOverviewDays} días."],
            });
        }

        var branches = branchIds is { Length: > 0 }
            ? branchIds.Distinct().ToList()
            : await db.Branches.AsNoTracking().Where(b => b.IsActive).Select(b => b.Id).ToListAsync(cancellationToken);
        var barbers = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.BranchId != null && branches.Contains(u.BranchId.Value))
            .Where(u => barberIds == null || barberIds.Length == 0 || barberIds.Contains(u.Id))
            .Select(u => new { u.Id, BranchId = u.BranchId!.Value })
            .ToListAsync(cancellationToken);
        var branchOf = barbers.ToDictionary(b => b.Id, b => b.BranchId);
        var ids = barbers.Select(b => b.Id).ToList();

        var start = opening.DayRange(from).Start;
        var end = opening.DayRange(to).End;
        var slots = await db.AvailableSlots.AsNoTracking()
            .Where(s => ids.Contains(s.BarberId) && s.StartAt >= start && s.StartAt < end)
            .ToListAsync(cancellationToken);
        var appointments = await BlockingAsync(ids, start, end, cancellationToken);
        var holidays = await db.Holidays.AsNoTracking()
            .Where(h => h.IsActive && h.Date >= from && h.Date <= to)
            .ToDictionaryAsync(h => h.Date, h => h.Name, cancellationToken);

        var byDay = slots.GroupBy(s => opening.DateOf(s.StartAt)).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<ScheduleDay>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var daySlots = byDay.GetValueOrDefault(day) ?? [];
            var perBranch = branches.Select(branchId =>
            {
                var own = daySlots.Where(s => branchOf[s.BarberId] == branchId).ToList();
                return Count(branchId, own, appointments);
            }).ToList();

            holidays.TryGetValue(day, out var holidayName);
            result.Add(new ScheduleDay(
                day,
                holidayName,
                holidayName is not null || !business.Value.GetOpeningRanges(day.DayOfWeek).Any(),
                perBranch.Sum(b => b.Free),
                perBranch.Sum(b => b.Busy),
                perBranch.Sum(b => b.Deleted),
                perBranch.Sum(b => b.Extra),
                perBranch));
        }

        return result;
    }

    public async Task<ScheduleDayDetail> DayAsync(DateOnly date, int barberId, CancellationToken cancellationToken)
    {
        var exists = await db.Users.AsNoTracking().AnyAsync(u => u.Id == barberId && u.Role == UserRole.Barber, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException("No se encontró el barbero.");
        }

        var (start, end) = opening.DayRange(date);
        var slots = await db.AvailableSlots.AsNoTracking()
            .Where(s => s.BarberId == barberId && s.StartAt >= start && s.StartAt < end)
            .OrderBy(s => s.StartAt)
            .ToListAsync(cancellationToken);
        var appointments = await BlockingAsync([barberId], start, end, cancellationToken);
        var holiday = await db.Holidays.AsNoTracking()
            .Where(h => h.IsActive && h.Date == date).Select(h => h.Name).FirstOrDefaultAsync(cancellationToken);

        var items = slots.Select(slot =>
        {
            var appointment = slot.DeletedAt is null ? appointments.FirstOrDefault(a => Covers(a, slot)) : null;
            var state = slot.DeletedAt is not null ? "deleted" : appointment is not null ? "busy" : "free";
            return new DaySlot(slot.Id, slot.StartAt, opening.TimeOf(slot.StartAt).ToString("HH:mm"), state, slot.Origin,
                appointment?.Id, slot.DeleteReason, slot.DeletedAt);
        }).ToList();

        return new ScheduleDayDetail(barberId, date, holiday, items);
    }

    public async Task<BranchChangePreview> PreviewBranchChangeAsync(int barberId, int newBranchId, CancellationToken cancellationToken)
    {
        var barber = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == barberId && u.Role == UserRole.Barber, cancellationToken)
                     ?? throw new NotFoundException("No se encontró el barbero.");
        var affected = await FutureOfBarberAsync(barberId, tracking: false, cancellationToken);
        var details = await DescribeAsync(affected, cancellationToken);
        return new BranchChangePreview(barberId, barber.BranchId ?? 0, newBranchId, details.Count,
            details.Where(d => d.Refundable).Sum(d => d.Price), details);
    }

    public async Task<IReadOnlyList<int>> CancelForBranchChangeAsync(int barberId, int adminId, CancellationToken cancellationToken)
    {
        var affected = await FutureOfBarberAsync(barberId, tracking: true, cancellationToken);
        var actor = new Actor(adminId, UserRole.Admin);
        var confirmed = new List<int>();
        foreach (var appointment in affected)
        {
            if (appointment.Status == AppointmentStatus.Pending)
            {
                confirmed.Add(appointment.Id);
            }

            await changes.CancelForBusinessReasonAsync(appointment, actor, ScheduleLimits.BranchChangeReason, cancellationToken);
        }

        return confirmed;
    }

    private Task<List<Appointment>> FutureOfBarberAsync(int barberId, bool tracking, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = tracking ? db.Appointments : db.Appointments.AsNoTracking();
        return query
            .Where(a => a.BarberId == barberId && CancellableStatuses.Contains(a.Status) && a.EndDate > now)
            .OrderBy(a => a.AppointmentDate)
            .ToListAsync(cancellationToken);
    }

    private async Task<Selection> ResolveAsync(SlotRangeRequest request, CancellationToken cancellationToken)
    {
        List<int> branchIds;
        if (request.AllBranches == true)
        {
            branchIds = await db.Branches.AsNoTracking().Where(b => b.IsActive).Select(b => b.Id).ToListAsync(cancellationToken);
        }
        else
        {
            branchIds = request.BranchIds!.ToList();
            var found = await db.Branches.AsNoTracking().CountAsync(b => branchIds.Contains(b.Id), cancellationToken);
            if (found != branchIds.Count)
            {
                throw new BadRequestException("Alguna de las sucursales elegidas no existe.", ErrorCodes.BranchInactive);
            }
        }

        var barbers = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.IsActive && u.BranchId != null && branchIds.Contains(u.BranchId.Value))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        if (request.BarberIds is { Length: > 0 })
        {
            var outside = request.BarberIds.Except(barbers).ToList();
            if (outside.Count > 0)
            {
                throw new BadRequestException(
                    "Algún barbero elegido no está activo o no pertenece a las sucursales elegidas.", ErrorCodes.BarberUnavailable);
            }

            barbers = request.BarberIds.ToList();
        }

        if (barbers.Count == 0)
        {
            throw new BadRequestException("No hay barberos activos en las sucursales elegidas.", ErrorCodes.BarberUnavailable);
        }

        return new Selection(branchIds, barbers, request);
    }

    private async Task<(List<AvailableSlot> Slots, List<Appointment> Affected)> LoadRemovalAsync(
        Selection selection, CancellationToken cancellationToken, bool tracking = false)
    {
        var range = selection.Range;
        var start = opening.DayRange(range.DateFrom!.Value).Start;
        var end = opening.DayRange(range.DateTo!.Value).End;
        var now = clock.UtcNow;
        var barberIds = selection.BarberIds.ToList();

        var query = tracking ? db.AvailableSlots : db.AvailableSlots.AsNoTracking();
        var slots = (await query
                .Where(s => barberIds.Contains(s.BarberId) && s.DeletedAt == null && s.StartAt >= start && s.StartAt < end && s.StartAt > now)
                .ToListAsync(cancellationToken))
            .Where(s => Matches(range, s.StartAt))
            .ToList();
        if (slots.Count == 0)
        {
            return (slots, []);
        }

        var first = slots.Min(s => s.StartAt);
        var last = slots.Max(s => s.StartAt) + opening.SlotLength;
        var appointmentsQuery = tracking ? db.Appointments : db.Appointments.AsNoTracking();
        var candidates = await appointmentsQuery
            .Where(a => barberIds.Contains(a.BarberId) && CancellableStatuses.Contains(a.Status) &&
                        a.AppointmentDate < last && a.EndDate > first)
            .ToListAsync(cancellationToken);
        var affected = candidates.Where(a => slots.Any(slot => Covers(a, slot))).OrderBy(a => a.AppointmentDate).ToList();
        return (slots, affected);
    }

    private async Task<(List<(int BarberId, DateTimeOffset Start)> Create, List<AvailableSlot> Restore, int Skip)> ClassifyAdditionAsync(
        Selection selection, CancellationToken cancellationToken, bool tracking)
    {
        var range = selection.Range;
        var now = clock.UtcNow;
        var starts = new List<DateTimeOffset>();
        for (var day = range.DateFrom!.Value; day <= range.DateTo!.Value; day = day.AddDays(1))
        {
            if (range.Weekdays is { Length: > 0 } && !range.Weekdays.Contains(day.DayOfWeek))
            {
                continue;
            }

            var dayStarts = range.TimeFrom is { } timeFrom && range.TimeTo is { } timeTo
                ? GridStarts(day, timeFrom, timeTo)
                : opening.BaseSlotStarts(day);
            starts.AddRange(dayStarts.Where(start => start > now));
        }

        if (starts.Count == 0)
        {
            return ([], [], 0);
        }

        var first = starts.Min();
        var last = starts.Max();
        var barberIds = selection.BarberIds.ToList();
        var query = tracking ? db.AvailableSlots : db.AvailableSlots.AsNoTracking();
        var existing = (await query
                .Where(s => barberIds.Contains(s.BarberId) && s.StartAt >= first && s.StartAt <= last)
                .ToListAsync(cancellationToken))
            .ToDictionary(s => (s.BarberId, s.StartAt.UtcTicks));

        var create = new List<(int, DateTimeOffset)>();
        var restore = new List<AvailableSlot>();
        var skip = 0;
        foreach (var barberId in barberIds)
        {
            foreach (var start in starts)
            {
                if (!existing.TryGetValue((barberId, start.UtcTicks), out var slot))
                {
                    create.Add((barberId, start));
                }
                else if (slot.DeletedAt is not null)
                {
                    restore.Add(slot);
                }
                else
                {
                    skip++;
                }
            }
        }

        return (create, restore, skip);
    }

    private IEnumerable<DateTimeOffset> GridStarts(DateOnly day, TimeOnly from, TimeOnly to)
    {
        var step = opening.SlotMinutes;
        var first = (int)from.ToTimeSpan().TotalMinutes;
        var last = (int)to.ToTimeSpan().TotalMinutes;
        for (var minute = first; minute + step <= last; minute += step)
        {
            yield return ToUtc(day, minute);
        }
    }

    private DateTimeOffset ToUtc(DateOnly day, int minute) =>
        minute >= 24 * 60
            ? clock.ToUtc(day.AddDays(1), TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute - 24 * 60)))
            : clock.ToUtc(day, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute)));

    private bool Matches(SlotRangeRequest range, DateTimeOffset start)
    {
        var date = opening.DateOf(start);
        if (range.Weekdays is { Length: > 0 } && !range.Weekdays.Contains(date.DayOfWeek))
        {
            return false;
        }

        if (range.TimeFrom is { } from && range.TimeTo is { } to)
        {
            var time = opening.TimeOf(start);
            return time >= from && time.AddMinutes(opening.SlotMinutes) <= to && time.AddMinutes(opening.SlotMinutes) > time;
        }

        return true;
    }

    private bool Covers(Appointment appointment, AvailableSlot slot) =>
        appointment.BarberId == slot.BarberId &&
        appointment.AppointmentDate < slot.StartAt + opening.SlotLength &&
        appointment.EndDate > slot.StartAt;

    private BranchDayCounts Count(int branchId, List<AvailableSlot> slots, List<Appointment> appointments)
    {
        var free = 0;
        var busy = 0;
        var deleted = 0;
        var extra = 0;
        foreach (var slot in slots)
        {
            if (slot.DeletedAt is not null)
            {
                deleted++;
                continue;
            }

            if (slot.Origin == SlotOrigin.Manual)
            {
                extra++;
            }

            if (appointments.Any(a => Covers(a, slot)))
            {
                busy++;
            }
            else
            {
                free++;
            }
        }

        return new BranchDayCounts(branchId, free, busy, deleted, extra);
    }

    private Task<List<Appointment>> BlockingAsync(List<int> barberIds, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        db.Appointments.AsNoTracking()
            .Where(a => barberIds.Contains(a.BarberId) && OpeningSchedule.BlockingStatuses.Contains(a.Status) &&
                        a.AppointmentDate < end && a.EndDate > start)
            .ToListAsync(cancellationToken);

    private async Task<List<AffectedAppointment>> DescribeAsync(List<Appointment> appointments, CancellationToken cancellationToken)
    {
        if (appointments.Count == 0)
        {
            return [];
        }

        var ids = appointments.Select(a => a.Id).ToList();
        var rows = await db.Appointments.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                BarberName = a.Barber!.Name,
                ClientName = a.Client != null ? a.Client.Name : a.ClientName,
                CutName = a.Cut!.Name,
            })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var result = new List<AffectedAppointment>(appointments.Count);
        foreach (var a in appointments)
        {
            var row = rows[a.Id];
            result.Add(new AffectedAppointment(
                a.Id, a.AppointmentDate, opening.TimeOf(a.AppointmentDate).ToString("HH:mm"),
                a.BarberId, row.BarberName, a.BranchId, row.ClientName ?? "Cliente", row.CutName,
                a.Status, a.PaymentStatus, a.Price,
                await changes.IsRefundableAsync(a, cancellationToken)));
        }

        return result;
    }

    private async Task<IDbContextTransaction?> BeginAsync(CancellationToken cancellationToken) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
}
