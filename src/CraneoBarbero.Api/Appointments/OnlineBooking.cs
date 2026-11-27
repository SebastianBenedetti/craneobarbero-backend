using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Appointments;

public sealed record OnlineBookingResult(Appointment Appointment, string? CheckoutUrl);

public sealed class OnlineBooking(
    AppDbContext db, AppointmentBooking booking, Payments.AppointmentPayments payments, AppointmentNotifications notifications)
{
    public const string EmailNotVerifiedMessage = "Verificá tu email para poder reservar turnos.";

    public async Task EnsureCanBookAsync(int clientId, CancellationToken cancellationToken)
    {
        var verified = await db.Users.AsNoTracking()
            .Where(u => u.Id == clientId)
            .Select(u => u.EmailVerified)
            .SingleAsync(cancellationToken);
        if (!verified)
        {
            throw new ForbiddenException(EmailNotVerifiedMessage, ErrorCodes.EmailNotVerified);
        }
    }

    public async Task<OnlineBookingResult> BookAsync(
        int clientId, int barberId, int cutId, DateTimeOffset start, int? branchId, CancellationToken cancellationToken,
        PaymentMethod paymentMethod = PaymentMethod.MercadoPago)
    {
        await EnsureCanBookAsync(clientId, cancellationToken);

        var appointment = await booking.BookAsync(new BookingRequest(
            barberId,
            cutId,
            start,
            branchId,
            clientId,
            null,
            UserRole.Client,
            clientId,
            paymentMethod,
            Online: true), cancellationToken);

        if (paymentMethod == PaymentMethod.Cash)
        {
            await notifications.SendConfirmationAsync(appointment.Id, cancellationToken);
            return new OnlineBookingResult(appointment, null);
        }

        var checkoutUrl = await payments.TryStartCheckoutAsync(appointment, cancellationToken);
        return new OnlineBookingResult(appointment, checkoutUrl);
    }
}
