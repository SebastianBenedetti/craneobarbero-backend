using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Reports;

public sealed record ReportQuery(DateOnly? From, DateOnly? To, int? BranchId, int? CategoryId, string? GroupBy, int? Limit);

public sealed record ReportPeriodResponse(DateOnly From, DateOnly To);

public sealed record PrepaidResponse(int Count, decimal Amount);

public sealed record AppointmentSummary(
    decimal Income,
    int IncomeCount,
    int Completed,
    int LateCancellationsCharged,
    PrepaidResponse Prepaid,
    IReadOnlyDictionary<string, int> ByStatus);

public sealed record ProductSummary(decimal Income, decimal Cost, decimal Profit, int Orders, int Units);

public sealed record AdminSummaryResponse(
    ReportPeriodResponse Period,
    int? BranchId,
    AppointmentSummary Appointments,
    ProductSummary Products,
    decimal TotalIncome);

public sealed record BranchReportRow(
    int BranchId,
    string Name,
    decimal AppointmentIncome,
    int AppointmentsCompleted,
    decimal ProductIncome,
    decimal ProductProfit,
    decimal TotalIncome);

public sealed record BarberReportRow(
    int BarberId,
    string Name,
    string? BranchName,
    decimal Income,
    int Completed,
    int Cancelled,
    PrepaidResponse Prepaid);

public sealed record ServiceReportRow(int CutId, string Name, int Completed, decimal Income);

public sealed record ProductReportRow(
    int ProductId,
    string Name,
    string Category,
    int Units,
    decimal Income,
    decimal Cost,
    decimal Profit,
    decimal? MarginPercent);

public sealed record CategoryReportRow(int CategoryId, string Name, int Units, decimal Income, decimal Cost, decimal Profit);

public sealed record ProductReportResponse(
    ReportPeriodResponse Period,
    IReadOnlyList<ProductReportRow> Products,
    IReadOnlyList<CategoryReportRow> Categories);

public sealed record PeriodReportRow(string Period, decimal AppointmentIncome, decimal ProductIncome, decimal TotalIncome);

public sealed record RegularClientRow(int? ClientId, string Name, int Completed, DateTimeOffset LastAppointmentDate);

public sealed record BarberSummaryResponse(
    ReportPeriodResponse Period,
    decimal Income,
    int IncomeCount,
    int Completed,
    PrepaidResponse Prepaid,
    IReadOnlyDictionary<string, int> ByStatus);

public static class ReportGrouping
{
    public const string Day = "day";
    public const string Month = "month";
}

public static class ReportLimits
{
    public const int MaxDays = 731;
    public const int DefaultTopClients = 10;
    public const int MaxTopClients = 50;
}

public sealed class ReportQueryValidator : AbstractValidator<ReportQuery>
{
    public ReportQueryValidator()
    {
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("La fecha final no puede ser anterior a la inicial.");
        RuleFor(q => q).Must(q => q.From is null || q.To is null || q.To.Value.DayNumber - q.From.Value.DayNumber < ReportLimits.MaxDays)
            .WithName("to").WithMessage("El período puede abarcar hasta dos años.");
        RuleFor(q => q.BranchId).GreaterThan(0).When(q => q.BranchId is not null).WithMessage("La sucursal no es válida.");
        RuleFor(q => q.CategoryId).GreaterThan(0).When(q => q.CategoryId is not null).WithMessage("La categoría no es válida.");
        RuleFor(q => q.GroupBy).Must(g => g is null or ReportGrouping.Day or ReportGrouping.Month)
            .WithMessage("Agrupá por día o por mes.");
        RuleFor(q => q.Limit).InclusiveBetween(1, ReportLimits.MaxTopClients).When(q => q.Limit is not null)
            .WithMessage($"Se pueden pedir hasta {ReportLimits.MaxTopClients} clientes.");
    }
}

internal static class StatusNames
{
    public static string Of(AppointmentStatus status) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(status.ToString());
}
