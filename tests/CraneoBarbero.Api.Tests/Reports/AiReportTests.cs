using System.Net;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Reports;
using CraneoBarbero.Api.Reports.Insights;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Reports;

public sealed class FakeReportWriter : IReportWriter
{
    public bool Configured { get; set; }
    public List<string> Requests { get; } = [];

    public bool IsConfigured => Configured;

    public Task<ReportNarrative?> WriteAsync(string data, CancellationToken cancellationToken)
    {
        Requests.Add(data);
        return Task.FromResult<ReportNarrative?>(Configured
            ? new ReportNarrative("Resumen de prueba.", "Ocupación de prueba.", [new ReportStrategy("Probar una promoción", "Hacer una promoción.", "El lunes está flojo.", "Difundirla en redes.", "Medir la ocupación."), new ReportStrategy("Sumar un combo", "Vender un combo.", "Se venden pocos productos.", "Mostrarlo en el mostrador.", "Medir las unidades.")], "Reseñas de prueba.", true)
            : null);
    }
}

public sealed class AiReportApiFactory : ScheduleApiFactory
{
    public const string Recipient = "duenio@example.com";

    public FakeReportWriter Writer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(MonthlyReportJob.RecipientsVariable, Recipient);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IReportWriter>();
            services.AddSingleton<IReportWriter>(Writer);
        });
    }
}

public sealed class AiReportTests(AiReportApiFactory factory) : IClassFixture<AiReportApiFactory>
{
    [Fact]
    public async Task Ai_report_is_only_for_the_admin()
    {
        const string url = "/reports/admin/ai-report";
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            await HttpAssertions.AssertErrorAsync(await (await factory.ClientForAsync(role)).GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Admin_downloads_the_monthly_pdf_with_the_ai_analysis()
    {
        factory.Writer.Configured = true;
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.GetAsync("/reports/admin/ai-report?from=2026-11-01&to=2026-11-23");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Reporte 2026-11 - Craneo Barbero.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("true", response.Headers.GetValues("X-Report-Ai").Single());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Contains("Ocupación", factory.Writer.Requests.Last());
    }

    [Fact]
    public async Task Without_ai_the_report_uses_rules_and_the_range_appears_in_the_name()
    {
        factory.Writer.Configured = false;
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var response = await admin.GetAsync("/reports/admin/ai-report?from=2026-09-01&to=2026-11-23");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("false", response.Headers.GetValues("X-Report-Ai").Single());
        Assert.Equal("Reporte 2026-09 a 2026-11 - Craneo Barbero.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task Rule_based_analysis_always_proposes_strategies()
    {
        using var scope = factory.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<ReportService>();
        var data = scope.ServiceProvider.GetRequiredService<InsightData>();

        var snapshot = await data.BuildAsync(reports.Scope(new ReportQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 23), null, null, null, null)), CancellationToken.None);
        var narrative = InsightText.RuleBased(snapshot);

        Assert.False(narrative.AiGenerated);
        Assert.True(narrative.Strategies.Count >= 2);
        Assert.All(narrative.Strategies, strategy =>
        {
            Assert.False(string.IsNullOrWhiteSpace(strategy.Reason));
            Assert.False(string.IsNullOrWhiteSpace(strategy.HowTo));
            Assert.False(string.IsNullOrWhiteSpace(strategy.Measure));
        });
        Assert.False(string.IsNullOrWhiteSpace(narrative.Summary));
    }

    [Fact]
    public async Task Last_working_day_skips_sundays_and_holidays()
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<MonthlyReportJob>();

        Assert.Equal(new DateOnly(2026, 5, 30), await job.LastWorkingDayAsync(2026, 5, CancellationToken.None));
        await factory.WithDbAsync(async db =>
        {
            db.Holidays.Add(new Holiday { Date = new DateOnly(2026, 3, 31), Name = "Feriado de prueba", Type = HolidayType.Custom, Source = "manual", IsActive = true });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(new DateOnly(2026, 3, 30), await job.LastWorkingDayAsync(2026, 3, CancellationToken.None));
    }

    [Fact]
    public async Task Monthly_report_is_sent_once_at_noon_on_the_last_working_day()
    {
        factory.Writer.Configured = true;
        try
        {
            factory.Time.SetUtcNow(Local(2026, 11, 30, 11, 55));
            Assert.False(await RunJobAsync());

            factory.Time.SetUtcNow(Local(2026, 11, 27, 21, 0));
            Assert.False(await RunJobAsync());

            factory.Time.SetUtcNow(Local(2026, 11, 30, 12, 5));
            Assert.True(await RunJobAsync());
            Assert.False(await RunJobAsync());

            var mail = factory.Emails.SentTo(AiReportApiFactory.Recipient).Single();
            Assert.Contains("Noviembre 2026", mail.Subject);
            Assert.Equal("Reporte 2026-11 - Craneo Barbero.pdf", mail.Attachments!.Single().FileName);
            var stored = await factory.WithDbAsync(db => db.MonthlyReports.AsNoTracking().SingleAsync(r => r.Month == "2026-11"));
            Assert.True(stored.AiGenerated);
            Assert.Equal(1, stored.EmailsSent);
            Assert.Equal(new DateOnly(2026, 11, 1), stored.PeriodFrom);
        }
        finally
        {
            factory.Time.SetUtcNow(Now);
        }
    }

    private async Task<bool> RunJobAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MonthlyReportJob>().RunAsync(CancellationToken.None);
    }
}
