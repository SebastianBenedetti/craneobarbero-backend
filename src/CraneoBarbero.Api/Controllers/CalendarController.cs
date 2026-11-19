using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Calendar;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
public sealed class CalendarController(AppDbContext db, AppointmentCalendar calendar, CalendarLinks links) : ControllerBase
{
    private const string NotFoundMessage = "No se encontró el turno.";

    [Authorize(Policy = Policies.Client)]
    [HttpGet("appointments/{id}/calendar.ics")]
    public async Task<IActionResult> ForOwner(int id, CancellationToken cancellationToken)
    {
        var appointment = await LoadAsync(id, cancellationToken) ?? throw new NotFoundException(NotFoundMessage);
        if (appointment.ClientId != User.GetRequiredUserId())
        {
            throw new ForbiddenException();
        }

        return Export(appointment);
    }

    [AllowAnonymous]
    [HttpGet("calendar/{token}.ics")]
    public async Task<IActionResult> ForSignedLink(string token, CancellationToken cancellationToken)
    {
        if (!links.TryRead(token, out var id))
        {
            throw new NotFoundException("El enlace del calendario no es válido o ya venció.");
        }

        var appointment = await LoadAsync(id, cancellationToken) ?? throw new NotFoundException(NotFoundMessage);
        return Export(appointment);
    }

    private Task<Appointment?> LoadAsync(int id, CancellationToken cancellationToken) =>
        db.Appointments.AsNoTracking()
            .Include(a => a.Barber).Include(a => a.Cut).Include(a => a.Branch)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    private FileContentResult Export(Appointment appointment)
    {
        if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.Cancelled))
        {
            throw new ConflictException("Solo se pueden agregar al calendario los turnos confirmados.", ErrorCodes.CalendarUnavailable);
        }

        Response.Headers.CacheControl = "no-store";
        return File(calendar.Build(appointment), AppointmentCalendar.ContentType, AppointmentCalendar.FileName);
    }
}
