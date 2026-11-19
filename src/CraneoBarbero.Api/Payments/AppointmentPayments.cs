using System.Globalization;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Payments;

public sealed class PaymentProviderException(string message)
    : AppException(StatusCodes.Status502BadGateway, ErrorCodes.PaymentProviderError, message);

public sealed record CheckoutResponse(int AppointmentId, string CheckoutUrl, DateTimeOffset? PaymentExpiresAt);

public sealed record PaymentReturnResponse(string? GatewayStatus, AppointmentResponse Appointment);

public sealed class AppointmentPayments(
    AppDbContext db,
    IPaymentGateway gateway,
    BusinessClock clock,
    ILogger<AppointmentPayments> logger) : IAppointmentRefunds
{
    public const string ReferencePrefix = "appointment:";
    public const string ProviderErrorMessage = "No se pudo comunicar con Mercado Pago. Probá de nuevo en unos minutos.";

    public static string ReferenceFor(int appointmentId) => ReferencePrefix + appointmentId.ToString(CultureInfo.InvariantCulture);

    public async Task<string?> TryStartCheckoutAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            logger.LogWarning("Mercado Pago no está configurado: el turno {AppointmentId} queda sin enlace de pago", appointment.Id);
            return null;
        }

        try
        {
            return await StartCheckoutAsync(appointment, cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            logger.LogError(exception, "No se pudo crear el pago del turno {AppointmentId}", appointment.Id);
            return null;
        }
    }

    public async Task<string> StartCheckoutAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            throw new PaymentProviderException("Los pagos online no están disponibles en este momento.");
        }

        if (appointment.Status != AppointmentStatus.AwaitingPayment ||
            appointment.PaymentExpiresAt is null ||
            appointment.PaymentExpiresAt <= clock.UtcNow)
        {
            throw new ConflictException("Este turno ya no se puede pagar.", ErrorCodes.NotPayable);
        }

        if (appointment.Price <= 0)
        {
            throw new ConflictException("Este turno no tiene un importe para pagar.", ErrorCodes.NotPayable);
        }

        await ReleaseCheckoutAsync(appointment, cancellationToken);

        var details = await db.Appointments.AsNoTracking()
            .Where(a => a.Id == appointment.Id)
            .Select(a => new { CutName = a.Cut!.Name, BarberName = a.Barber!.Name, ClientEmail = a.Client != null ? a.Client.Email : null })
            .SingleAsync(cancellationToken);

        CreatedPreference preference;
        try
        {
            preference = await gateway.CreatePreferenceAsync(new PreferenceRequest(
                ReferenceFor(appointment.Id),
                new PreferenceItem($"turno-{appointment.Id}", $"{details.CutName} con {details.BarberName}", appointment.Price),
                appointment.PaymentExpiresAt.Value,
                details.ClientEmail), cancellationToken);
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogError(exception, "Mercado Pago rechazó la preferencia del turno {AppointmentId}", appointment.Id);
            throw new PaymentProviderException(ProviderErrorMessage);
        }

        db.Payments.Add(new Payment
        {
            AppointmentId = appointment.Id,
            Amount = appointment.Price,
            Method = PaymentMethod.MercadoPago,
            Status = TransactionStatus.Pending,
            PreferenceId = preference.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        return preference.CheckoutUrl;
    }

    public async Task<int?> ProcessGatewayPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        GatewayPayment? remote;
        try
        {
            remote = await gateway.GetPaymentAsync(paymentId, cancellationToken);
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogError(exception, "No se pudo consultar el pago {PaymentId} en Mercado Pago", paymentId);
            throw new PaymentProviderException(ProviderErrorMessage);
        }

        if (remote is null || !TryParseReference(remote.ExternalReference, out var appointmentId))
        {
            logger.LogWarning("Pago {PaymentId} sin turno asociado", paymentId);
            return null;
        }

        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
        if (appointment is null)
        {
            logger.LogWarning("El pago {PaymentId} apunta a un turno inexistente ({AppointmentId})", paymentId, appointmentId);
            return null;
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.ExternalId == remote.Id, cancellationToken)
                      ?? await db.Payments
                          .Where(p => p.AppointmentId == appointmentId && p.ExternalId == null && p.Status == TransactionStatus.Pending)
                          .OrderByDescending(p => p.Id)
                          .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            payment = new Payment { AppointmentId = appointmentId, Method = PaymentMethod.MercadoPago, Amount = Math.Max(remote.Amount, 0.01m) };
            db.Payments.Add(payment);
        }

        var alreadyApproved = payment.ExternalId == remote.Id && payment.Status is TransactionStatus.Approved or TransactionStatus.Refunded;
        payment.ExternalId = remote.Id;

        switch (remote.Status)
        {
            case "approved" when !alreadyApproved:
                payment.Status = TransactionStatus.Approved;
                payment.Amount = remote.Amount > 0 ? remote.Amount : payment.Amount;
                payment.PaidAt = remote.ApprovedAt ?? clock.UtcNow;
                await ApplyApprovalAsync(appointment, payment, remote, cancellationToken);
                break;
            case "rejected" or "cancelled" when payment.Status == TransactionStatus.Pending:
                payment.Status = TransactionStatus.Rejected;
                break;
            case "refunded" or "charged_back":
                payment.Status = TransactionStatus.Refunded;
                payment.RefundedAt ??= clock.UtcNow;
                break;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("El pago {PaymentId} ya había sido procesado", paymentId);
        }

        return appointmentId;
    }

    public async Task<bool> RefundAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        var payment = await db.Payments
            .Where(p => p.AppointmentId == appointment.Id && p.Status == TransactionStatus.Approved)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            return false;
        }

        if (payment.ExternalId is not null)
        {
            try
            {
                await gateway.RefundAsync(payment.ExternalId, cancellationToken);
            }
            catch (PaymentGatewayException exception)
            {
                logger.LogError(exception, "Falló el reembolso del pago {PaymentId} del turno {AppointmentId}", payment.ExternalId, appointment.Id);
                throw new PaymentProviderException("No se pudo hacer el reembolso en Mercado Pago. Probá de nuevo en unos minutos.");
            }
        }

        payment.Status = TransactionStatus.Refunded;
        payment.RefundedAt = clock.UtcNow;
        return true;
    }

    public async Task ReleaseCheckoutAsync(Appointment appointment, CancellationToken cancellationToken)
    {
        var pending = await db.Payments
            .Where(p => p.AppointmentId == appointment.Id && p.Status == TransactionStatus.Pending && p.ExternalId == null && p.PreferenceId != null)
            .ToListAsync(cancellationToken);
        foreach (var payment in pending)
        {
            if (gateway.IsConfigured)
            {
                try
                {
                    await gateway.ExpirePreferenceAsync(payment.PreferenceId!, cancellationToken);
                }
                catch (PaymentGatewayException exception)
                {
                    logger.LogWarning(exception, "No se pudo anular la preferencia {PreferenceId}", payment.PreferenceId);
                }
            }

            payment.Status = TransactionStatus.Rejected;
        }
    }

    public static bool TryParseReference(string? reference, out int appointmentId)
    {
        appointmentId = 0;
        return reference is not null &&
               reference.StartsWith(ReferencePrefix, StringComparison.Ordinal) &&
               int.TryParse(reference[ReferencePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out appointmentId) &&
               appointmentId > 0;
    }

    private async Task ApplyApprovalAsync(Appointment appointment, Payment payment, GatewayPayment remote, CancellationToken cancellationToken)
    {
        if (appointment.Status == AppointmentStatus.AwaitingPayment && remote.Amount >= appointment.Price)
        {
            appointment.Status = AppointmentStatus.Pending;
            appointment.PaymentStatus = PaymentStatus.Paid;
            appointment.PaymentMethod = PaymentMethod.MercadoPago;
            appointment.PaidAt = payment.PaidAt;
            appointment.PaymentExpiresAt = null;
            return;
        }

        if (appointment.Status == AppointmentStatus.Pending && appointment.PaymentStatus == PaymentStatus.Paid)
        {
            logger.LogWarning("Pago duplicado {PaymentId} para el turno {AppointmentId}: se reembolsa", remote.Id, appointment.Id);
        }
        else
        {
            logger.LogWarning(
                "Pago {PaymentId} aprobado para el turno {AppointmentId} en estado {Status}: se reembolsa",
                remote.Id, appointment.Id, appointment.Status);
        }

        try
        {
            await gateway.RefundAsync(remote.Id, cancellationToken);
            payment.Status = TransactionStatus.Refunded;
            payment.RefundedAt = clock.UtcNow;
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogError(exception, "No se pudo reembolsar el pago {PaymentId}; revisarlo a mano en Mercado Pago", remote.Id);
        }
    }
}
