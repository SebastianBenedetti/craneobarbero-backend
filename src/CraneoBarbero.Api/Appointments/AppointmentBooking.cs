using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CraneoBarbero.Api.Appointments;

public sealed record BookingRequest(
    int BarberId,
    int CutId,
    DateTimeOffset Start,
    int? BranchId,
    int? ClientId,
    string? ClientName,
    UserRole CreatedByRole,
    int CreatedById,
    PaymentMethod PaymentMethod,
    bool Online);

public sealed class AppointmentBooking(
    AppDbContext db,
    AvailabilityService availability,
    OpeningSchedule opening,
    BusinessClock clock,
    IOptions<BusinessSettings> business)
{
    public const string SlotUnavailableMessage = "El horario ya no está disponible. Elegí otro.";

    public async Task<Appointment> BookAsync(BookingRequest request, CancellationToken cancellationToken)
    {
        var barber = await db.Users.AsNoTracking()
                         .Include(u => u.Branch)
                         .FirstOrDefaultAsync(u => u.Id == request.BarberId && u.Role == UserRole.Barber, cancellationToken)
                     ?? throw new NotFoundException("No se encontró el barbero.");
        if (!barber.IsActive || barber.Branch is null || !barber.Branch.IsActive)
        {
            throw new BadRequestException("El barbero elegido no está disponible.", ErrorCodes.BarberUnavailable);
        }

        if (request.BranchId is not null && request.BranchId != barber.BranchId)
        {
            throw new BadRequestException("El barbero no atiende en la sucursal elegida.", ErrorCodes.BarberUnavailable);
        }

        var cut = await db.Cuts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CutId, cancellationToken);
        if (cut is null || !cut.IsActive)
        {
            throw new BadRequestException("El servicio elegido no está disponible.", ErrorCodes.CutUnavailable);
        }

        var start = request.Start.ToUniversalTime();
        var date = opening.DateOf(start);
        var forBarber = new AvailabilityQuery(barber.Id, cut.DurationMinutes, date, ApplyBookingWindow: request.Online);
        if (!await availability.IsAvailableAsync(forBarber, start, cancellationToken))
        {
            throw new ConflictException(SlotUnavailableMessage, ErrorCodes.SlotUnavailable);
        }

        if (request.ClientId is not null &&
            !await availability.IsAvailableAsync(forBarber with { ClientId = request.ClientId }, start, cancellationToken))
        {
            throw ClientOverlap(request.Online);
        }

        var now = clock.UtcNow;
        var waitsForPayment = request.Online && request.PaymentMethod == PaymentMethod.MercadoPago;
        var appointment = new Appointment
        {
            AppointmentDate = start,
            EndDate = start.AddMinutes(cut.DurationMinutes),
            Status = waitsForPayment ? AppointmentStatus.AwaitingPayment : AppointmentStatus.Pending,
            ClientId = request.ClientId,
            ClientName = request.ClientId is null ? request.ClientName : null,
            BarberId = barber.Id,
            CutId = cut.Id,
            BranchId = barber.BranchId!.Value,
            Price = cut.Price,
            PaymentStatus = PaymentStatus.Unpaid,
            PaymentMethod = request.PaymentMethod,
            PaymentExpiresAt = waitsForPayment ? now.AddMinutes(business.Value.PaymentExpirationMinutes) : null,
            CreatedByRole = request.CreatedByRole,
            CreatedById = request.CreatedById,
        };
        db.Appointments.Add(appointment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } postgres)
        {
            db.ChangeTracker.Clear();
            throw postgres.ConstraintName == Data.Configurations.AppointmentsConfiguration.ClientOverlapConstraint
                ? ClientOverlap(request.Online)
                : new ConflictException(SlotUnavailableMessage, ErrorCodes.SlotUnavailable);
        }

        return appointment;
    }

    private static ConflictException ClientOverlap(bool online) => new(
        online ? "Ya tenés otro turno en ese horario." : "El cliente ya tiene otro turno en ese horario.",
        ErrorCodes.ClientOverlap);
}
