namespace CraneoBarbero.Api.Reports.Export;

public static class ReportNames
{
    public static string FileName(ReportScope scope, string extension) =>
        SameMonth(scope)
            ? $"Reporte {scope.To:yyyy-MM} - Craneo Barbero.{extension}"
            : $"Reporte {scope.From:yyyy-MM} a {scope.To:yyyy-MM} - Craneo Barbero.{extension}";

    public static string Period(ReportScope scope) =>
        SameMonth(scope)
            ? $"{scope.To:MM-yyyy}"
            : $"{scope.From:MM-yyyy} a {scope.To:MM-yyyy}";

    private static bool SameMonth(ReportScope scope) =>
        scope.From.Year == scope.To.Year && scope.From.Month == scope.To.Month;
}
