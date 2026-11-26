using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Reports;
using CraneoBarbero.Api.Reports.Export;
using CraneoBarbero.Api.Reports.Insights;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("reports")]
public sealed class ReportsController(
    ReportService reports, ReportExporter exporter, AiReportService aiReports, AiReportQuota aiQuota) : ControllerBase
{
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/summary")]
    public Task<AdminSummaryResponse> AdminSummary([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.AdminSummaryAsync(reports.Scope(query), cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/branches")]
    public Task<IReadOnlyList<BranchReportRow>> Branches([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByBranchAsync(reports.Scope(query), cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/barbers")]
    public Task<IReadOnlyList<BarberReportRow>> Barbers([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByBarberAsync(reports.Scope(query), cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/services")]
    public Task<IReadOnlyList<ServiceReportRow>> Services([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByServiceAsync(reports.Scope(query), cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/products")]
    public Task<ProductReportResponse> Products([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByProductAsync(reports.Scope(query), cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/periods")]
    public Task<IReadOnlyList<PeriodReportRow>> Periods([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByPeriodAsync(reports.Scope(query), query.GroupBy, includeProducts: true, cancellationToken);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/export")]
    public async Task<IActionResult> Export([FromQuery] string? format, [FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var normalized = format?.Trim().ToLowerInvariant();
        if (normalized is not (ReportExporter.Pdf or ReportExporter.Xlsx))
        {
            throw new BadRequestException("Elegí el formato: pdf o xlsx.");
        }

        var file = await exporter.ExportAsync(reports.Scope(query), normalized, cancellationToken);
        return File(file.Content, file.ContentType, file.FileName);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin/ai-report")]
    public async Task<IActionResult> AiReport([FromQuery] ReportQuery query, CancellationToken cancellationToken)
    {
        var result = await aiReports.GenerateAsync(reports.Scope(query), aiQuota.TryUse(), cancellationToken);
        Response.Headers["X-Report-Ai"] = result.Narrative.AiGenerated ? "true" : "false";
        return File(result.File.Content, result.File.ContentType, result.File.FileName);
    }

    [Authorize(Policy = Policies.Barber)]
    [HttpGet("barber/summary")]
    public Task<BarberSummaryResponse> BarberSummary([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.BarberSummaryAsync(BarberScope(query), cancellationToken);

    [Authorize(Policy = Policies.Barber)]
    [HttpGet("barber/services")]
    public Task<IReadOnlyList<ServiceReportRow>> BarberServices([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByServiceAsync(BarberScope(query), cancellationToken);

    [Authorize(Policy = Policies.Barber)]
    [HttpGet("barber/clients")]
    public Task<IReadOnlyList<RegularClientRow>> BarberClients([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.RegularClientsAsync(BarberScope(query), query.Limit ?? ReportLimits.DefaultTopClients, cancellationToken);

    [Authorize(Policy = Policies.Barber)]
    [HttpGet("barber/periods")]
    public Task<IReadOnlyList<PeriodReportRow>> BarberPeriods([FromQuery] ReportQuery query, CancellationToken cancellationToken) =>
        reports.ByPeriodAsync(BarberScope(query), query.GroupBy, includeProducts: false, cancellationToken);

    private ReportScope BarberScope(ReportQuery query) =>
        reports.Scope(query with { BranchId = null, CategoryId = null }, User.GetRequiredUserId());
}
