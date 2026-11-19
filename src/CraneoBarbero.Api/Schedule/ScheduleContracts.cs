using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Schedule;

public record SlotRangeRequest(
    int[]? BranchIds,
    bool? AllBranches,
    int[]? BarberIds,
    DateOnly? DateFrom,
    DateOnly? DateTo,
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    DayOfWeek[]? Weekdays);

public sealed record RemoveSlotsRequest(
    int[]? BranchIds,
    bool? AllBranches,
    int[]? BarberIds,
    DateOnly? DateFrom,
    DateOnly? DateTo,
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    DayOfWeek[]? Weekdays,
    string? Reason,
    bool? CancelAffected,
    int? ExpectedCancellations)
    : SlotRangeRequest(BranchIds, AllBranches, BarberIds, DateFrom, DateTo, TimeFrom, TimeTo, Weekdays);

public sealed record AffectedAppointment(
    int Id,
    DateTimeOffset AppointmentDate,
    string Time,
    int BarberId,
    string BarberName,
    int BranchId,
    string ClientName,
    string CutName,
    AppointmentStatus Status,
    PaymentStatus PaymentStatus,
    decimal Price,
    bool Refundable);

public sealed record RemoveSlotsPreview(
    int SlotsToRemove,
    int SlotsWithAppointments,
    int Branches,
    int Barbers,
    int AffectedCount,
    decimal TotalRefund,
    IReadOnlyList<AffectedAppointment> AffectedAppointments);

public sealed record RemoveSlotsResult(int Removed, int KeptWithAppointments, int CancelledAppointments, int Refunded, decimal TotalRefunded);

public sealed record AddSlotsPreview(int ToCreate, int ToRestore, int ToSkip, int Branches, int Barbers);

public sealed record AddSlotsResult(int Created, int Restored, int Skipped);

public sealed record BranchDayCounts(int BranchId, int Free, int Busy, int Deleted, int Extra);

public sealed record ScheduleDay(
    DateOnly Date,
    string? HolidayName,
    bool ClosedByDefault,
    int Free,
    int Busy,
    int Deleted,
    int Extra,
    IReadOnlyList<BranchDayCounts> Branches);

public sealed record DaySlot(
    int Id,
    DateTimeOffset StartAt,
    string Time,
    string State,
    SlotOrigin Origin,
    int? AppointmentId,
    string? DeleteReason,
    DateTimeOffset? DeletedAt);

public sealed record ScheduleDayDetail(int BarberId, DateOnly Date, string? HolidayName, IReadOnlyList<DaySlot> Slots);

public sealed record BranchChangePreview(
    int BarberId,
    int CurrentBranchId,
    int NewBranchId,
    int AffectedCount,
    decimal TotalRefund,
    IReadOnlyList<AffectedAppointment> AffectedAppointments);

public static class ScheduleLimits
{
    public const int MaxRangeDays = 366;
    public const int MaxOverviewDays = 31;
    public const string ScheduleRemovedReason = "schedule_removed";
    public const string BranchChangeReason = "barber_branch_change";
}

public class SlotRangeRequestValidator<T> : AbstractValidator<T> where T : SlotRangeRequest
{
    public SlotRangeRequestValidator(BusinessClock clock, Microsoft.Extensions.Options.IOptions<BusinessSettings> business)
    {
        var slot = business.Value.SlotMinutes;

        RuleFor(r => r.BranchIds)
            .Must((request, ids) => request.AllBranches == true || (ids is { Length: > 0 }))
            .WithMessage("Elegí al menos una sucursal o \"Seleccionar todas\".");
        RuleFor(r => r.BranchIds).Must(ids => ids is null || ids.All(id => id > 0) && ids.Distinct().Count() == ids.Length)
            .WithMessage("Las sucursales no son válidas.");
        RuleFor(r => r.BarberIds).Must(ids => ids is null || ids.All(id => id > 0) && ids.Distinct().Count() == ids.Length)
            .WithMessage("Los barberos no son válidos.");
        RuleFor(r => r.DateFrom).NotNull().WithMessage("Elegí la fecha de inicio.")
            .Must(date => date is null || date.Value >= clock.Today).WithMessage("No se puede operar sobre fechas pasadas.");
        RuleFor(r => r.DateTo).NotNull().WithMessage("Elegí la fecha de fin.");
        RuleFor(r => r.DateTo)
            .Must((request, to) => request.DateFrom is null || to is null || to.Value >= request.DateFrom.Value)
            .WithMessage("La fecha de fin no puede ser anterior a la de inicio.")
            .Must((request, to) => request.DateFrom is null || to is null || to.Value.DayNumber - request.DateFrom.Value.DayNumber < ScheduleLimits.MaxRangeDays)
            .WithMessage($"El rango no puede superar {ScheduleLimits.MaxRangeDays} días.");
        RuleFor(r => r.TimeFrom).Must(time => time is null || OnGrid(time.Value, slot))
            .WithMessage($"La hora de inicio tiene que ser en punto o y media (bloques de {slot} min).");
        RuleFor(r => r.TimeTo).Must(time => time is null || OnGrid(time.Value, slot))
            .WithMessage($"La hora de fin tiene que ser en punto o y media (bloques de {slot} min).");
        RuleFor(r => r.TimeTo)
            .Must((request, to) => request.TimeFrom is null || to is null || to.Value > request.TimeFrom.Value)
            .WithMessage("La hora de fin tiene que ser posterior a la de inicio.");
        RuleFor(r => r.TimeTo)
            .Must((request, to) => (request.TimeFrom is null) == (to is null))
            .WithMessage("Indicá la hora de inicio y la de fin, o ninguna.");
        RuleFor(r => r.Weekdays).Must(days => days is null || days.Distinct().Count() == days.Length)
            .WithMessage("Hay días de la semana repetidos.");
    }

    private static bool OnGrid(TimeOnly time, int slot) =>
        time.Second == 0 && time.Millisecond == 0 && time.Minute % slot == 0;
}

public sealed class SlotRangeRequestValidator(BusinessClock clock, Microsoft.Extensions.Options.IOptions<BusinessSettings> business)
    : SlotRangeRequestValidator<SlotRangeRequest>(clock, business);

public sealed class RemoveSlotsRequestValidator : SlotRangeRequestValidator<RemoveSlotsRequest>
{
    public RemoveSlotsRequestValidator(BusinessClock clock, Microsoft.Extensions.Options.IOptions<BusinessSettings> business)
        : base(clock, business)
    {
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Escribí el motivo.")
            .Must(reason => reason is null || reason.Trim().Length is >= 3 and <= 300)
            .WithMessage("El motivo debe tener entre 3 y 300 caracteres.");
        RuleFor(r => r.CancelAffected).NotNull().WithMessage("Elegí si se mantienen o se cancelan los turnos afectados.");
        RuleFor(r => r.ExpectedCancellations).NotNull().When(r => r.CancelAffected == true)
            .WithMessage("Confirmá la cantidad de turnos que se cancelan.");
    }
}
