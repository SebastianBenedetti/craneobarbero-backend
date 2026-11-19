using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Schedule;

public sealed class ScheduleJobsService(
    IServiceScopeFactory scopes,
    BusinessClock clock,
    IOptions<ScheduleSettings> options,
    ILogger<ScheduleJobsService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _lastHolidaySync;
    private DateOnly? _lastGenerationDay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.JobsEnabled)
        {
            logger.LogInformation("Los procesos automáticos de la agenda están desactivados");
            return;
        }

        await RunDueJobsAsync(startup: true, stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunDueJobsAsync(startup: false, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunDueJobsAsync(bool startup, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.Now;

        if (startup || _lastHolidaySync is null || now - _lastHolidaySync >= TimeSpan.FromDays(settings.HolidaySyncIntervalDays))
        {
            await RunAsync("sincronización de feriados", async services =>
            {
                var result = await services.GetRequiredService<HolidayService>().SyncAsync(cancellationToken);
                logger.LogInformation(
                    "Feriados sincronizados: {Added} nuevos, {Renamed} renombrados, {Removed} quitados, {SlotsRemoved} horarios dados de baja, años sin respuesta {FailedYears}",
                    result.Added, result.Renamed, result.Removed, result.SlotsRemoved, result.FailedYears);
                if (result.FailedYears.Count == 0)
                {
                    _lastHolidaySync = now;
                }
            }, cancellationToken);
        }

        var today = DateOnly.FromDateTime(now.DateTime);
        var generationDue = startup ||
                            (_lastGenerationDay != today && TimeOnly.FromDateTime(now.DateTime) >= settings.DailyGenerationTime);
        if (generationDue)
        {
            await RunAsync("generación de horarios", async services =>
            {
                var generator = services.GetRequiredService<SlotGenerator>();
                var result = await generator.GenerateAsync(cancellationToken: cancellationToken);
                var deleted = await generator.DeleteOldPastSlotsAsync(cancellationToken);
                logger.LogInformation(
                    "Horarios generados: {Created} nuevos para {Barbers} barberos; {Deleted} horarios viejos borrados",
                    result.Created, result.Barbers, deleted);
                _lastGenerationDay = today;
            }, cancellationToken);
        }
    }

    private async Task RunAsync(string name, Func<IServiceProvider, Task> job, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await job(scope.ServiceProvider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falló la {Job}; se reintenta en la próxima pasada", name);
        }
    }
}
