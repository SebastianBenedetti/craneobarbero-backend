using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CraneoBarbero.Api.Appointments;

public sealed record CancelAppointmentRequest(string? Reason);

public sealed record RescheduleAppointmentRequest(
    DateTimeOffset? AppointmentDate,
    PaymentMethod? PaymentMethod,
    int? ClientId,
    string? ClientName);

public sealed record CancellationPreview(
    bool WithinWindow,
    double HoursLeft,
    bool Refundable,
    decimal Amount,
    decimal KeptAmount,
    AppointmentStatus ResultingStatus,
    PaymentStatus ResultingPaymentStatus,
    string Message);

public sealed record Actor(int Id, UserRole Role);

public sealed class CancelAppointmentRequestValidator : AbstractValidator<CancelAppointmentRequest>
{
    public CancelAppointmentRequestValidator()
    {
        RuleFor(r => r.Reason).MaximumLength(300).WithMessage("El motivo admite hasta 300 caracteres.");
    }
}

public sealed class RescheduleAppointmentRequestValidator : AbstractValidator<RescheduleAppointmentRequest>
{
    public RescheduleAppointmentRequestValidator()
    {
        RuleFor(r => r.AppointmentDate).NotNull().WithMessage("Elegí la nueva fecha y horario.");
        RuleFor(r => r.PaymentMethod)
            .Must(method => method is null || AppointmentLimits.ManualPaymentMethods.Contains(method.Value))
            .WithMessage("El medio de pago debe ser efectivo, tarjeta, transferencia o billetera virtual.");
        RuleFor(r => r.ClientId).GreaterThan(0).When(r => r.ClientId is not null).WithMessage("El cliente no es válido.");
        RuleFor(r => r.ClientName)
            .Must(name => name is null || name.Trim().Length is >= 2 and <= 100)
            .WithMessage("El nombre debe tener entre 2 y 100 caracteres.");
    }
}

public interface IAppointmentRefunds
{
    Task<bool> RefundAsync(Appointment appointment, CancellationToken cancellationToken);

    Task ReleaseCheckoutAsync(Appointment appointment, CancellationToken cancellationToken);
}

public sealed class AppointmentChanges(
    AppDbContext db,
    AvailabilityService availability,
    OpeningSchedule opening,
    BusinessClock clock,
    IAppointmentRefunds refunds,
    IOptions<BusinessSettings> business)
{
    public const string NotFoundMessage = "No se encontró el turno.";

    private int WindowHours => business.Value.CancellationWindowHours;

    public async Task<Appointment> FindForAsync(int id, Actor actor, CancellationToken cancellationToken)
    {
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                          ?? throw new NotFoundException(NotFoundMessage);

        var allowed = actor.Role switch
        {
            UserRole.Admin => true,
            UserRole.Barber => appointment.BarberId == actor.Id,
            UserRole.Client => appointment.ClientId == actor.Id,
            _ => false,
        };
        if (!allowed)
        {
            throw new ForbiddenException();
        }

        return appointment;
    }

    public async Task<CancellationPreview> PreviewCancellationAsync(Appointment appointment, Actor actor, CancellationToken cancellationToken)
    {
        EnsureCancellable(appointment, actor);
        var now = clock.UtcNow;
        var hoursLeft = (appointment.AppointmentDate - now).TotalHours;
        var withinWindow = hoursLeft >= WindowHours;

        if (appointment.Status == AppointmentStatus.AwaitingPayment)
        {
            return new CancellationPreview(withinWindow, Round(hoursLeft), false, 0m, 0m,
                AppointmentStatus.Cancelled, PaymentStatus.Unpaid,
                "El turno todavía no se pagó: se cancela sin ningún cobro.");
        }

        if (appointment.PaymentStatus != PaymentStatus.Paid)
        {
            return new CancellationPreview(withinWindow, Round(hoursLeft), false, 0m, 0m,
                AppointmentStatus.Cancelled, PaymentStatus.Unpaid,
                "El turno está pendiente de abonar: se cancela sin ningún cobro ni reembolso.");
        }

        var paidOnline = await HasApprovedOnlinePaymentAsync(appointment, cancellationToken);
        if (withinWindow && paidOnline)
        {
            return new CancellationPreview(true, Round(hoursLeft), true, appointment.Price, 0m,
                AppointmentStatus.Cancelled, PaymentStatus.Refunded,
                actor.Role == UserRole.Client
                    ? $"Faltan más de {WindowHours} horas: se te reembolsa el total del pago."
                    : $"Se cumple la anticipación de {WindowHours} horas: se reembolsará el total del pago.");
        }

        return new CancellationPreview(withinWindow, Round(hoursLeft), false, 0m, appointment.Price,
            AppointmentStatus.Cancelled, PaymentStatus.Paid,
            actor.Role == UserRole.Client
                ? $"Al faltar menos de {WindowHours} horas para tu turno, si lo cancelás no se realizará el reembolso del pago. ¿Querés cancelarlo igualmente?"
                : withinWindow
                    ? "El pago no se hizo por la pasarela: no habrá reembolso."
                    : $"No se cumple la anticipación de {WindowHours} horas: no habrá reembolso.");
    }

    public async Task<Appointment> CancelAsync(Appointment appointment, Actor actor, string? reason, CancellationToken cancellationToken)
    {
        var preview = await PreviewCancellationAsync(appointment, actor, cancellationToken);

        var paymentStatus = preview.ResultingPaymentStatus;
        if (preview.Refundable && !await refunds.RefundAsync(appointment, cancellationToken))
        {
            paymentStatus = PaymentStatus.Paid;
        }

        if (appointment.Status == AppointmentStatus.AwaitingPayment)
        {
            await refunds.ReleaseCheckoutAsync(appointment, cancellationToken);
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.PaymentStatus = paymentStatus;
        appointment.PaymentExpiresAt = null;
        appointment.CancelledAt = clock.UtcNow;
        appointment.CancelledById = actor.Id;
        appointment.CancelledByRole = actor.Role;
        appointment.CancellationReason = AccountRules.TrimToNull(reason);

        await db.SaveChangesAsync(cancellationToken);
        return appointment;
    }

    public async Task<bool> CancelForBusinessReasonAsync(Appointment appointment, Actor actor, string reason, CancellationToken cancellationToken)
    {
        var refunded = false;
        if (appointment.Status == AppointmentStatus.Pending && appointment.PaymentStatus == PaymentStatus.Paid)
        {
            try
            {
                refunded = await refunds.RefundAsync(appointment, cancellationToken);
            }
            catch (AppException)
            {
                refunded = false;
            }
        }
        else if (appointment.Status == AppointmentStatus.AwaitingPayment)
        {
            await refunds.ReleaseCheckoutAsync(appointment, cancellationToken);
        }

        if (refunded)
        {
            appointment.PaymentStatus = PaymentStatus.Refunded;
        }
        else if (appointment.Status == AppointmentStatus.AwaitingPayment)
        {
            appointment.PaymentStatus = PaymentStatus.Unpaid;
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.PaymentExpiresAt = null;
        appointment.CancelledAt = clock.UtcNow;
        appointment.CancelledById = actor.Id;
        appointment.CancelledByRole = actor.Role;
        appointment.CancellationReason = reason;
        return refunded;
    }

    public Task<bool> IsRefundableAsync(Appointment appointment, CancellationToken cancellationToken) =>
        appointment.Status == AppointmentStatus.Pending && appointment.PaymentStatus == PaymentStatus.Paid
            ? HasApprovedOnlinePaymentAsync(appointment, cancellationToken)
            : Task.FromResult(false);

    public async Task<Appointment> RescheduleAsync(
        Appointment appointment, Actor actor, RescheduleAppointmentRequest request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        if (appointment.Status != AppointmentStatus.Pending)
        {
            throw new ConflictException(
                appointment.Status == AppointmentStatus.AwaitingPayment
                    ? "El turno todavía está esperando el pago: no se puede modificar."
                    : "El turno ya no se puede modificar.",
                ErrorCodes.NotModifiable);
        }

        if (appointment.AppointmentDate <= now)
        {
            throw new ConflictException("El turno ya empezó: no se puede modificar.", ErrorCodes.NotModifiable);
        }

        var isClient = actor.Role == UserRole.Client;
        if (isClient && (appointment.AppointmentDate - now).TotalHours < WindowHours)
        {
            throw new ConflictException(
                $"Faltan menos de {WindowHours} horas para tu turno: ya no se puede modificar. Si no podés asistir, podés cancelarlo.",
                ErrorCodes.ModificationWindowClosed);
        }

        var isManual = appointment.CreatedByRole != UserRole.Client;
        var changesManualFields = !isClient &&
                                  (request.PaymentMethod is not null || request.ClientId is not null || request.ClientName is not null);
        if (changesManualFields && !isManual)
        {
            throw new BadRequestException(
                "El medio de pago y el cliente solo se pueden corregir en turnos cargados a mano.", ErrorCodes.ManualFieldsOnly);
        }

        var clientId = appointment.ClientId;
        var clientName = appointment.ClientName;
        if (changesManualFields)
        {
            if (request.ClientId is not null)
            {
                var clientOk = await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == request.ClientId && u.Role == UserRole.Client && u.IsActive, cancellationToken);
                if (!clientOk)
                {
                    throw new BadRequestException("El cliente elegido no existe o está desactivado.", ErrorCodes.ClientUnavailable);
                }

                clientId = request.ClientId;
                clientName = null;
            }
            else if (AccountRules.TrimToNull(request.ClientName) is { } name)
            {
                clientId = null;
                clientName = name;
            }
        }

        var start = request.AppointmentDate!.Value.ToUniversalTime();
        var duration = appointment.EndDate - appointment.AppointmentDate;
        if (start != appointment.AppointmentDate)
        {
            var query = new AvailabilityQuery(
                appointment.BarberId,
                (int)duration.TotalMinutes,
                opening.DateOf(start),
                ApplyBookingWindow: isClient,
                IgnoreAppointmentId: appointment.Id);
            if (!await availability.IsAvailableAsync(query, start, cancellationToken))
            {
                throw new ConflictException(AppointmentBooking.SlotUnavailableMessage, ErrorCodes.SlotUnavailable);
            }
        }

        if (clientId is not null)
        {
            var end = start + duration;
            var overlaps = await db.Appointments.AsNoTracking().AnyAsync(a =>
                a.Id != appointment.Id &&
                a.ClientId == clientId &&
                OpeningSchedule.BlockingStatuses.Contains(a.Status) &&
                a.AppointmentDate < end && a.EndDate > start, cancellationToken);
            if (overlaps)
            {
                throw new ConflictException(
                    isClient ? "Ya tenés otro turno en ese horario." : "El cliente ya tiene otro turno en ese horario.",
                    ErrorCodes.ClientOverlap);
            }
        }

        appointment.AppointmentDate = start;
        appointment.EndDate = start + duration;
        appointment.ClientId = clientId;
        appointment.ClientName = clientName;
        if (changesManualFields && request.PaymentMethod is not null)
        {
            appointment.PaymentMethod = request.PaymentMethod;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } postgres)
        {
            throw postgres.ConstraintName == Data.Configurations.AppointmentsConfiguration.ClientOverlapConstraint
                ? new ConflictException(isClient ? "Ya tenés otro turno en ese horario." : "El cliente ya tiene otro turno en ese horario.", ErrorCodes.ClientOverlap)
                : new ConflictException(AppointmentBooking.SlotUnavailableMessage, ErrorCodes.SlotUnavailable);
        }

        return appointment;
    }

    private void EnsureCancellable(Appointment appointment, Actor actor)
    {
        if (appointment.Status is not (AppointmentStatus.Pending or AppointmentStatus.AwaitingPayment))
        {
            throw new ConflictException("El turno ya no se puede cancelar.", ErrorCodes.NotCancellable);
        }

        if (appointment.AppointmentDate <= clock.UtcNow)
        {
            throw new ConflictException("El turno ya empezó: no se puede cancelar.", ErrorCodes.NotCancellable);
        }

        if (appointment.Status == AppointmentStatus.AwaitingPayment && actor.Role == UserRole.Barber)
        {
            throw new ForbiddenException("Un turno que espera el pago solo lo puede cancelar el cliente o un admin.");
        }
    }

    private Task<bool> HasApprovedOnlinePaymentAsync(Appointment appointment, CancellationToken cancellationToken) =>
        db.Payments.AsNoTracking().AnyAsync(p =>
            p.AppointmentId == appointment.Id &&
            p.Status == TransactionStatus.Approved &&
            p.Method == PaymentMethod.MercadoPago, cancellationToken);

    private static double Round(double hours) => Math.Round(Math.Max(hours, 0), 1);
}
