using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Appointments;

public sealed record LifecycleResult(int Expired, int Completed, int CompletedAndPaid);

public sealed class AppointmentLifecycle(AppDbContext db, BusinessClock clock)
{
    public async Task<LifecycleResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var expired = await ExpireUnpaidAsync(now, cancellationToken);
        var (completed, paid) = await CompleteFinishedAsync(now, cancellationToken);
        return new LifecycleResult(expired, completed, paid);
    }

    private async Task<int> ExpireUnpaidAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = db.Appointments.Where(a =>
            a.Status == AppointmentStatus.AwaitingPayment && a.PaymentExpiresAt != null && a.PaymentExpiresAt < now);

        if (db.Database.IsRelational())
        {
            return await due.ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, AppointmentStatus.Expired)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken);
        }

        var rows = await due.ToListAsync(cancellationToken);
        rows.ForEach(a => a.Status = AppointmentStatus.Expired);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    private async Task<(int Completed, int Paid)> CompleteFinishedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var finished = db.Appointments.Where(a => a.Status == AppointmentStatus.Pending && a.EndDate <= now);
        var unpaid = finished.Where(a => a.PaymentStatus == PaymentStatus.Unpaid);
        var alreadyPaid = finished.Where(a => a.PaymentStatus != PaymentStatus.Unpaid);

        if (db.Database.IsRelational())
        {
            var paid = await unpaid.ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, AppointmentStatus.Completed)
                .SetProperty(a => a.PaymentStatus, PaymentStatus.Paid)
                .SetProperty(a => a.PaidAt, now)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken);
            var others = await alreadyPaid.ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Status, AppointmentStatus.Completed)
                .SetProperty(a => a.UpdatedAt, now), cancellationToken);
            return (paid + others, paid);
        }

        var unpaidRows = await unpaid.ToListAsync(cancellationToken);
        foreach (var appointment in unpaidRows)
        {
            appointment.Status = AppointmentStatus.Completed;
            appointment.PaymentStatus = PaymentStatus.Paid;
            appointment.PaidAt = now;
        }

        var paidRows = await alreadyPaid.ToListAsync(cancellationToken);
        paidRows.ForEach(a => a.Status = AppointmentStatus.Completed);
        await db.SaveChangesAsync(cancellationToken);
        return (unpaidRows.Count + paidRows.Count, unpaidRows.Count);
    }
}

public sealed class AppointmentJobsService(
    IServiceScopeFactory scopes,
    IOptions<ScheduleSettings> options,
    ILogger<AppointmentJobsService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobsEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<AppointmentLifecycle>().RunAsync(stoppingToken);
                if (result.Expired + result.Completed > 0)
                {
                    logger.LogInformation(
                        "Turnos actualizados: {Expired} vencidos sin pago, {Completed} completados ({Paid} manuales pasados a abonados)",
                        result.Expired, result.Completed, result.CompletedAndPaid);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falló la actualización automática de turnos; se reintenta en un minuto");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
