using CraneoBarbero.Api.Calendar;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Appointments;

public sealed class AppointmentNotifications(
    AppDbContext db,
    IEmailQueue queue,
    AppointmentCalendar calendar,
    CalendarLinks links,
    BusinessClock clock,
    ILogger<AppointmentNotifications> logger)
{
    public Task<bool> SendConfirmationAsync(int appointmentId, CancellationToken cancellationToken) =>
        SendAsync(appointmentId, NotificationType.Confirmation, null, null, cancellationToken);

    public Task<bool> SendModificationAsync(int appointmentId, CancellationToken cancellationToken) =>
        SendAsync(appointmentId, NotificationType.Modification, null, null, cancellationToken);

    public Task<bool> SendCancellationAsync(int appointmentId, string? detail, CancellationToken cancellationToken) =>
        SendAsync(appointmentId, NotificationType.Cancellation, detail, null, cancellationToken);

    public Task<bool> SendReminderAsync(int appointmentId, DateTimeOffset scheduledAt, CancellationToken cancellationToken) =>
        SendAsync(appointmentId, NotificationType.Reminder, null, scheduledAt, cancellationToken);

    public async Task SendCancellationsAsync(IEnumerable<int> appointmentIds, string? detail, CancellationToken cancellationToken)
    {
        foreach (var id in appointmentIds)
        {
            await SendCancellationAsync(id, detail, cancellationToken);
        }
    }

    private async Task<bool> SendAsync(
        int appointmentId, NotificationType type, string? detail, DateTimeOffset? scheduledAt, CancellationToken cancellationToken)
    {
        try
        {
            var appointment = await db.Appointments.AsNoTracking()
                .Include(a => a.Client).Include(a => a.Barber).Include(a => a.Cut).Include(a => a.Branch)
                .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);
            if (appointment?.Client is not { IsActive: true, EmailVerified: true } client ||
                appointment.Barber is null || appointment.Cut is null || appointment.Branch is null)
            {
                return false;
            }

            var expectedStatus = type == NotificationType.Cancellation ? AppointmentStatus.Cancelled : AppointmentStatus.Pending;
            if (appointment.Status != expectedStatus)
            {
                return false;
            }

            var message = await BuildAsync(appointment, client, type, detail, cancellationToken);
            var notification = new Notification
            {
                UserId = client.Id,
                AppointmentId = appointment.Id,
                Type = type,
                Channel = NotificationChannel.Email,
                Status = NotificationStatus.Pending,
                ScheduledAt = scheduledAt ?? clock.UtcNow,
            };
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(cancellationToken);
            queue.Enqueue(new QueuedEmail(notification.Id, message));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "No se pudo preparar el mail {Type} del turno {AppointmentId}", type, appointmentId);
            return false;
        }
    }

    private async Task<EmailMessage> BuildAsync(
        Appointment appointment, User client, NotificationType type, string? detail, CancellationToken cancellationToken)
    {
        var details = new AppointmentEmailDetails(
            client.Name,
            clock.ToBusinessTime(appointment.AppointmentDate),
            appointment.Branch!.Name,
            appointment.Branch.Address,
            appointment.Branch.Phone,
            appointment.Barber!.Name,
            appointment.Cut!.Name);
        EmailAttachment? attachment = null;

        switch (type)
        {
            case NotificationType.Confirmation:
                var paymentLine = appointment.PaymentMethod == PaymentMethod.MercadoPago && appointment.PaymentStatus == PaymentStatus.Paid
                    ? $"Pago: {AppointmentEmailTemplates.FormatMoney(appointment.Price)} acreditado con Mercado Pago."
                    : null;
                return AppointmentEmailTemplates.Confirmation(client.Email, details, paymentLine, CalendarButtons(appointment), attachment);
            case NotificationType.Modification:
                return AppointmentEmailTemplates.Modification(client.Email, details, CalendarButtons(appointment), attachment);
            case NotificationType.Reminder:
                return AppointmentEmailTemplates.Reminder(client.Email, details, CalendarButtons(appointment), attachment);
            default:
                var (headline, lines) = await DescribeCancellationAsync(appointment, client, detail, cancellationToken);
                return AppointmentEmailTemplates.Cancellation(client.Email, details, headline, lines, attachment);
        }
    }

    private List<EmailLink> CalendarButtons(Appointment appointment)
    {
        var result = new List<EmailLink>();
        if (links.CreateUrl(appointment.Id, appointment.EndDate) is { } url)
        {
            result.Add(new EmailLink("Agregar al calendario", url));
        }

        result.Add(new EmailLink("Google Calendar", calendar.GoogleCalendarUrl(appointment)));
        return result;
    }

    private async Task<(string Headline, List<string> Lines)> DescribeCancellationAsync(
        Appointment appointment, User client, string? detail, CancellationToken cancellationToken)
    {
        var byClient = appointment.CancelledByRole == UserRole.Client;
        var headline = byClient
            ? $"{client.Name}, cancelaste tu turno en Cráneo Barbero"
            : $"{client.Name}, tu turno en Cráneo Barbero fue cancelado";

        var lines = new List<string>();
        var reason = appointment.CancellationReason switch
        {
            ScheduleLimits.ScheduleRemovedReason => "La barbería no va a atender en ese horario" +
                                                    (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail.Trim()}."),
            ScheduleLimits.BranchChangeReason => "El barbero pasó a atender en otra sucursal.",
            _ when !byClient && !string.IsNullOrWhiteSpace(appointment.CancellationReason) => $"Motivo: {appointment.CancellationReason}",
            _ => null,
        };
        if (reason is not null)
        {
            lines.Add(reason);
        }

        var paidOnline = await db.Payments.AsNoTracking().AnyAsync(p =>
            p.AppointmentId == appointment.Id && p.Method == PaymentMethod.MercadoPago &&
            (p.Status == TransactionStatus.Approved || p.Status == TransactionStatus.Refunded), cancellationToken);

        if (appointment.PaymentStatus == PaymentStatus.Refunded)
        {
            lines.Add($"Te devolvemos {AppointmentEmailTemplates.FormatMoney(appointment.Price)} al medio de pago que usaste en Mercado Pago. Puede tardar unos días en verse reflejado.");
        }
        else if (paidOnline && appointment.PaymentStatus == PaymentStatus.Paid)
        {
            lines.Add(byClient
                ? "Como faltaban menos de 4 horas para el turno, el pago no se reembolsa."
                : "No pudimos procesar el reembolso automáticamente: nos vamos a comunicar con vos para devolverte el pago.");
        }

        return (headline, lines);
    }
}

public sealed record ReminderRunResult(int Sent);

public sealed class AppointmentReminders(AppDbContext db, AppointmentNotifications notifications, BusinessClock clock)
{
    public static readonly TimeSpan Advance = TimeSpan.FromHours(24);

    public async Task<ReminderRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var limit = now + Advance;
        var candidates = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Pending && a.ClientId != null &&
                        a.Client!.IsActive && a.Client.EmailVerified &&
                        a.AppointmentDate > now && a.AppointmentDate <= limit)
            .Select(a => new { a.Id, a.AppointmentDate, a.CreatedAt })
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var candidate in candidates)
        {
            var scheduledAt = candidate.AppointmentDate - Advance;
            if (scheduledAt < candidate.CreatedAt)
            {
                continue;
            }

            var alreadySent = await db.Notifications.AsNoTracking().AnyAsync(n =>
                n.AppointmentId == candidate.Id && n.Type == NotificationType.Reminder && n.ScheduledAt == scheduledAt, cancellationToken);
            if (!alreadySent && await notifications.SendReminderAsync(candidate.Id, scheduledAt, cancellationToken))
            {
                sent++;
            }
        }

        return new ReminderRunResult(sent);
    }
}
