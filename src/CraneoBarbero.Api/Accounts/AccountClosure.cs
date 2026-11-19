using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Accounts;

public sealed record ClosureAppointment(
    int Id,
    DateTimeOffset AppointmentDate,
    string BarberName,
    string CutName,
    AppointmentStatus Status,
    PaymentStatus PaymentStatus,
    decimal Price,
    bool Refundable,
    decimal RefundAmount);

public sealed record ClosurePreview(
    int FutureAppointments,
    int RefundableAppointments,
    decimal TotalRefund,
    IReadOnlyList<ClosureAppointment> Appointments);

public sealed record ClosureResult(int CancelledAppointments, int RefundedAppointments, decimal TotalRefunded);

public static class AccountClosureReasons
{
    public const string Deactivated = "account_deactivated";
    public const string Deleted = "account_deleted";
    public const string DeletedClientName = "Cliente eliminado";
}

public sealed class AccountClosure(
    AppDbContext db,
    AppointmentChanges changes,
    AppointmentNotifications notifications,
    BusinessClock clock)
{
    public async Task<ClosurePreview> PreviewAsync(int clientId, Actor actor, CancellationToken cancellationToken)
    {
        var upcoming = await UpcomingAsync(clientId, tracking: false, cancellationToken);
        var items = new List<ClosureAppointment>();
        foreach (var appointment in upcoming)
        {
            var preview = await changes.PreviewCancellationAsync(appointment, actor, cancellationToken);
            items.Add(new ClosureAppointment(
                appointment.Id,
                appointment.AppointmentDate,
                appointment.Barber?.Name ?? string.Empty,
                appointment.Cut?.Name ?? string.Empty,
                appointment.Status,
                appointment.PaymentStatus,
                appointment.Price,
                preview.Refundable,
                preview.Amount));
        }

        return new ClosurePreview(items.Count, items.Count(i => i.Refundable), items.Sum(i => i.RefundAmount), items);
    }

    public async Task<ClosureResult> DeactivateAsync(User client, Actor actor, CancellationToken cancellationToken)
    {
        EnsureClient(client);
        var (result, confirmedIds) = await CancelUpcomingAsync(client.Id, actor, AccountClosureReasons.Deactivated, cancellationToken);
        await notifications.SendCancellationsAsync(confirmedIds, null, cancellationToken);

        client.IsActive = false;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<ClosureResult> DeleteAsync(User client, Actor actor, CancellationToken cancellationToken)
    {
        EnsureClient(client);
        var (result, _) = await CancelUpcomingAsync(client.Id, actor, AccountClosureReasons.Deleted, cancellationToken);

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var history = await db.Appointments.Where(a => a.ClientId == client.Id).ToListAsync(cancellationToken);
        foreach (var appointment in history)
        {
            appointment.ClientId = null;
            appointment.ClientName = AccountClosureReasons.DeletedClientName;
        }

        var tokens = await db.AuthTokens.Where(t => t.UserId == client.Id).ToListAsync(cancellationToken);
        db.AuthTokens.RemoveRange(tokens);
        var userNotifications = await db.Notifications.Where(n => n.UserId == client.Id).ToListAsync(cancellationToken);
        db.Notifications.RemoveRange(userNotifications);
        var orders = await db.Orders.Where(o => o.ClientId == client.Id).ToListAsync(cancellationToken);
        foreach (var order in orders)
        {
            order.ClientId = null;
        }

        db.Users.Remove(client);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }

    private async Task<(ClosureResult Result, List<int> ConfirmedIds)> CancelUpcomingAsync(
        int clientId, Actor actor, string reason, CancellationToken cancellationToken)
    {
        var upcoming = await UpcomingAsync(clientId, tracking: true, cancellationToken);
        var confirmed = new List<int>();
        var refunded = 0;
        var total = 0m;
        foreach (var appointment in upcoming)
        {
            var wasConfirmed = appointment.Status == AppointmentStatus.Pending;
            await changes.CancelAsync(appointment, actor, reason, cancellationToken);
            if (wasConfirmed)
            {
                confirmed.Add(appointment.Id);
            }

            if (appointment.PaymentStatus == PaymentStatus.Refunded)
            {
                refunded++;
                total += appointment.Price;
            }
        }

        return (new ClosureResult(upcoming.Count, refunded, total), confirmed);
    }

    private Task<List<Appointment>> UpcomingAsync(int clientId, bool tracking, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var query = tracking ? db.Appointments : db.Appointments.AsNoTracking();
        return query
            .Include(a => a.Barber)
            .Include(a => a.Cut)
            .Where(a => a.ClientId == clientId &&
                        (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.AwaitingPayment) &&
                        a.AppointmentDate > now)
            .OrderBy(a => a.AppointmentDate)
            .ToListAsync(cancellationToken);
    }

    private static void EnsureClient(User user)
    {
        if (user.Role != UserRole.Client)
        {
            throw new BadRequestException("Esta acción solo se aplica a cuentas de clientes.", ErrorCodes.NotAClient);
        }
    }
}
