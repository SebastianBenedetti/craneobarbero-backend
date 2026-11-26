using System.Globalization;
using System.Net;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Reports.Insights;

public sealed class MonthlyReportJob(
    AppDbContext db,
    BusinessClock clock,
    IOptions<BusinessSettings> business,
    ReportService reports,
    AiReportService aiReports,
    IEmailSender emails,
    IConfiguration configuration,
    ILogger<MonthlyReportJob> logger)
{
    public const string RecipientsVariable = "MONTHLY_REPORT_EMAILS";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public async Task<DateOnly> LastWorkingDayAsync(int year, int month, CancellationToken cancellationToken)
    {
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var holidays = await db.Holidays.AsNoTracking()
            .Where(h => h.IsActive && h.Date >= first && h.Date <= last)
            .Select(h => h.Date)
            .ToListAsync(cancellationToken);
        var day = last;
        while (day > first && (business.Value.GetOpeningRanges(day.DayOfWeek).Count == 0 || holidays.Contains(day)))
        {
            day = day.AddDays(-1);
        }

        return day;
    }

    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.Now;
        var today = clock.Today;
        if (today != await LastWorkingDayAsync(today.Year, today.Month, cancellationToken))
        {
            return false;
        }

        var ranges = business.Value.GetOpeningRanges(today.DayOfWeek);
        if (ranges.Count == 0 || TimeOnly.FromDateTime(now.DateTime) < ranges.Max(r => r.End))
        {
            return false;
        }

        var monthKey = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (await db.MonthlyReports.AnyAsync(r => r.Month == monthKey, cancellationToken))
        {
            return false;
        }

        var record = new MonthlyReport { Month = monthKey, PeriodFrom = new DateOnly(today.Year, today.Month, 1), PeriodTo = today };
        db.MonthlyReports.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return false;
        }

        var scope = reports.Scope(new ReportQuery(record.PeriodFrom, record.PeriodTo, null, null, null, null));
        var result = await aiReports.GenerateAsync(scope, useAi: true, cancellationToken);
        record.AiGenerated = result.Narrative.AiGenerated;

        var recipients = (configuration[RecipientsVariable] ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var monthName = Spanish.TextInfo.ToTitleCase(today.ToString("MMMM yyyy", Spanish));
        foreach (var recipient in recipients)
        {
            var message = new EmailMessage(
                recipient,
                "Cráneo Barbero",
                $"Reporte mensual de Cráneo Barbero · {monthName}",
                $"<p>Hola,</p><p>Te mandamos el reporte mensual de <strong>{WebUtility.HtmlEncode(monthName)}</strong> con el análisis de ingresos, ocupación de turnos, estrategias recomendadas y reseñas.</p><p>Lo encontrás adjunto en PDF.</p><p>Cráneo Barbero</p>",
                $"Hola,\n\nTe mandamos el reporte mensual de {monthName} con el análisis de ingresos, ocupación de turnos, estrategias recomendadas y reseñas. Lo encontrás adjunto en PDF.\n\nCráneo Barbero",
                [new EmailAttachment(result.File.FileName, result.File.Content)]);
            if (await emails.SendAsync(message, cancellationToken))
            {
                record.EmailsSent++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Reporte mensual {Month} generado ({Ai}); mails enviados: {Sent} de {Total}",
            monthKey, record.AiGenerated ? "con IA" : "con reglas", record.EmailsSent, recipients.Count);
        return true;
    }
}

public sealed class MonthlyReportJobsService(
    IServiceScopeFactory scopes,
    IOptions<ScheduleSettings> options,
    ILogger<MonthlyReportJobsService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

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
                await scope.ServiceProvider.GetRequiredService<MonthlyReportJob>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falló el reporte mensual automático; se reintenta en la próxima pasada");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
