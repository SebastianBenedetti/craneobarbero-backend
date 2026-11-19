using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

public sealed record AvailabilityTime(DateTimeOffset StartAt, string Time);

public sealed record AvailabilityResponse(
    int BarberId,
    int CutId,
    DateOnly Date,
    int DurationMinutes,
    IReadOnlyList<AvailabilityTime> Times);

public sealed record CreateHolidayRequest(DateOnly? Date, string? Name);

public sealed record UpdateHolidayRequest(string? Name, bool? IsActive);

public sealed record HolidayResponse(
    int Id,
    DateOnly Date,
    string Name,
    HolidayType Type,
    string Source,
    bool IsActive,
    int AppointmentsCount);

public sealed record HolidayChangeResponse(HolidayResponse Holiday, int SlotsRemoved, int SlotsKeptWithAppointments, int SlotsRestored);

public sealed class CreateHolidayRequestValidator : AbstractValidator<CreateHolidayRequest>
{
    public CreateHolidayRequestValidator(BusinessClock clock)
    {
        RuleFor(r => r.Date).NotNull().WithMessage("Elegí la fecha.")
            .Must(date => date is null || date.Value >= clock.Today).WithMessage("No se pueden cargar días no laborables en el pasado.")
            .Must(date => date is null || date.Value <= clock.Today.AddYears(2)).WithMessage("La fecha está demasiado lejos.");
        RuleFor(r => r.Name).HolidayName();
    }
}

public sealed class UpdateHolidayRequestValidator : AbstractValidator<UpdateHolidayRequest>
{
    public UpdateHolidayRequestValidator()
    {
        RuleFor(r => r.Name).HolidayName();
        RuleFor(r => r.IsActive).NotNull().WithMessage("Indicá si el feriado está activo.");
    }
}

internal static class HolidayRules
{
    public static IRuleBuilderOptions<T, string?> HolidayName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Ingresá el nombre o motivo.")
            .Must(name => name is not null && name.Trim().Length is >= 3 and <= 150)
            .WithMessage("El nombre debe tener entre 3 y 150 caracteres.");
}

[ApiController]
[Route("appointments")]
public sealed class AvailabilityController(AppDbContext db, AvailabilityService availability, OpeningSchedule opening)
    : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("availability")]
    public async Task<AvailabilityResponse> Get(
        [FromQuery] int? barberId,
        [FromQuery] int? cutId,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (barberId is null or <= 0) errors["barberId"] = ["Elegí un barbero."];
        if (cutId is null or <= 0) errors["cutId"] = ["Elegí un servicio."];
        if (date is null) errors["date"] = ["Elegí una fecha (formato AAAA-MM-DD)."];
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        var barberAvailable = await db.Users.AsNoTracking().AnyAsync(u =>
            u.Id == barberId && u.Role == UserRole.Barber && u.IsActive && u.Branch != null && u.Branch.IsActive, cancellationToken);
        if (!barberAvailable)
        {
            throw new NotFoundException("No se encontró el barbero.");
        }

        var cut = await db.Cuts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cutId && c.IsActive, cancellationToken)
                  ?? throw new NotFoundException("No se encontró el servicio.");

        var isStaff = User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Barber);
        int? clientId = User.IsInRole(Roles.Client) ? User.GetUserId() : null;

        var starts = await availability.GetAvailableStartsAsync(
            new AvailabilityQuery(barberId!.Value, cut.DurationMinutes, date!.Value, ApplyBookingWindow: !isStaff, ClientId: clientId),
            cancellationToken);

        return new AvailabilityResponse(
            barberId.Value,
            cut.Id,
            date.Value,
            cut.DurationMinutes,
            starts.Select(start => new AvailabilityTime(start, opening.TimeOf(start).ToString("HH:mm"))).ToList());
    }
}

[ApiController]
[Route("holidays")]
[Authorize(Policy = Policies.Admin)]
public sealed class HolidaysController(
    AppDbContext db,
    HolidayService holidays,
    SlotGenerator generator,
    BusinessClock clock) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<HolidayResponse>> List([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var selectedYear = year ?? clock.Today.Year;
        if (selectedYear is < 2000 or > 2100)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["year"] = ["El año no es válido."] });
        }

        var first = new DateOnly(selectedYear, 1, 1);
        var last = new DateOnly(selectedYear, 12, 31);
        var rows = await db.Holidays.AsNoTracking()
            .Where(h => h.Date >= first && h.Date <= last)
            .OrderBy(h => h.Date)
            .ToListAsync(cancellationToken);

        var result = new List<HolidayResponse>(rows.Count);
        foreach (var holiday in rows)
        {
            result.Add(await ToResponseAsync(holiday, cancellationToken));
        }

        return result;
    }

    [HttpPost]
    public async Task<ActionResult<HolidayChangeResponse>> Create(CreateHolidayRequest request, CancellationToken cancellationToken)
    {
        var date = request.Date!.Value;
        var existing = await db.Holidays.AsNoTracking().FirstOrDefaultAsync(h => h.Date == date, cancellationToken);
        if (existing is not null)
        {
            throw new ConflictException(
                existing.IsActive
                    ? $"Ese día ya figura como no laborable ({existing.Name})."
                    : $"Ese día ya está cargado ({existing.Name}) pero desactivado: reactivalo desde la lista.",
                ErrorCodes.HolidayExists);
        }

        var holiday = new Holiday
        {
            Date = date,
            Name = request.Name!.Trim(),
            Type = HolidayType.Custom,
            Source = HolidayService.ManualSource,
            IsActive = true,
        };
        db.Holidays.Add(holiday);
        var change = await holidays.CloseDayAsync(date, User.GetUserId(), cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("Ese día ya figura como no laborable.", ErrorCodes.HolidayExists);
        }

        var response = new HolidayChangeResponse(await ToResponseAsync(holiday, cancellationToken),
            change.SlotsRemoved, change.SlotsKeptWithAppointments, 0);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPut("{id}")]
    public async Task<HolidayChangeResponse> Update(int id, UpdateHolidayRequest request, CancellationToken cancellationToken)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == id, cancellationToken)
                      ?? throw new NotFoundException("No se encontró el feriado.");

        holiday.Name = request.Name!.Trim();
        var activate = request.IsActive!.Value;
        var change = new HolidaySlotChange(0, 0, 0);

        if (activate != holiday.IsActive)
        {
            holiday.IsActive = activate;
            if (activate)
            {
                change = await holidays.CloseDayAsync(holiday.Date, User.GetUserId(), cancellationToken);
            }
            else
            {
                var restored = await holidays.ReopenDayAsync(holiday.Date, cancellationToken);
                change = new HolidaySlotChange(0, 0, restored);
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (!activate && holiday.Date >= clock.Today)
        {
            var generated = await generator.GenerateAsync(from: holiday.Date, to: holiday.Date, cancellationToken: cancellationToken);
            change = change with { SlotsRestored = change.SlotsRestored + generated.Created };
        }

        var saved = await db.Holidays.AsNoTracking().SingleAsync(h => h.Id == id, cancellationToken);
        return new HolidayChangeResponse(await ToResponseAsync(saved, cancellationToken),
            change.SlotsRemoved, change.SlotsKeptWithAppointments, change.SlotsRestored);
    }

    [HttpPost("sync")]
    public Task<HolidaySyncResult> Sync(CancellationToken cancellationToken) => holidays.SyncAsync(cancellationToken);

    private async Task<HolidayResponse> ToResponseAsync(Holiday holiday, CancellationToken cancellationToken) =>
        new(holiday.Id, holiday.Date, holiday.Name, holiday.Type, holiday.Source, holiday.IsActive,
            await holidays.CountAppointmentsAsync(holiday.Date, cancellationToken));
}

[ApiController]
[Route("schedule")]
[Authorize(Policy = Policies.Admin)]
public sealed class ScheduleController(ScheduleManagement schedule) : ControllerBase
{
    [HttpGet("overview")]
    public Task<IReadOnlyList<ScheduleDay>> Overview(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int[]? branchIds,
        [FromQuery] int[]? barberIds,
        CancellationToken cancellationToken)
    {
        if (from is null || to is null)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["from"] = ["Indicá el rango de fechas (from y to, formato AAAA-MM-DD)."],
            });
        }

        return schedule.OverviewAsync(from.Value, to.Value, branchIds, barberIds, cancellationToken);
    }

    [HttpGet("day")]
    public Task<ScheduleDayDetail> Day([FromQuery] DateOnly? date, [FromQuery] int? barberId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (date is null) errors["date"] = ["Indicá el día (formato AAAA-MM-DD)."];
        if (barberId is null or <= 0) errors["barberId"] = ["Elegí un barbero."];
        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return schedule.DayAsync(date!.Value, barberId!.Value, cancellationToken);
    }

    [HttpPost("slots/remove/preview")]
    public Task<RemoveSlotsPreview> PreviewRemove(SlotRangeRequest request, CancellationToken cancellationToken) =>
        schedule.PreviewRemovalAsync(request, cancellationToken);

    [HttpPost("slots/remove")]
    public Task<RemoveSlotsResult> Remove(RemoveSlotsRequest request, CancellationToken cancellationToken) =>
        schedule.RemoveAsync(request, User.GetRequiredUserId(), cancellationToken);

    [HttpPost("slots/add/preview")]
    public Task<AddSlotsPreview> PreviewAdd(SlotRangeRequest request, CancellationToken cancellationToken) =>
        schedule.PreviewAdditionAsync(request, cancellationToken);

    [HttpPost("slots/add")]
    public Task<AddSlotsResult> Add(SlotRangeRequest request, CancellationToken cancellationToken) =>
        schedule.AddAsync(request, User.GetRequiredUserId(), cancellationToken);
}
