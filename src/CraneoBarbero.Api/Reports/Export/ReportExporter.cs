using System.Globalization;
using CraneoBarbero.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record ExportedFile(byte[] Content, string ContentType, string FileName);

public sealed class ReportExporter(ReportService reports, AppDbContext db)
{
    public const string Pdf = "pdf";
    public const string Xlsx = "xlsx";

    private const string Title = "Reporte de Cráneo Barbero";
    private const string IncomeNote =
        "Cuentan como ingreso los turnos pagados que se completaron o se cancelaron sin reembolso, y los pedidos pagados, " +
        "listos, entregados o cancelados sin reembolso. Los turnos pagados por adelantado todavía no cuentan. " +
        "La ganancia de productos usa el costo guardado al momento de cada venta.";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public async Task<ExportedFile> ExportAsync(ReportScope scope, string format, CancellationToken cancellationToken)
    {
        var summary = await reports.AdminSummaryAsync(scope, cancellationToken);
        var branches = await reports.ByBranchAsync(scope, cancellationToken);
        var barbers = await reports.ByBarberAsync(scope, cancellationToken);
        var services = await reports.ByServiceAsync(scope, cancellationToken);
        var products = await reports.ByProductAsync(scope, cancellationToken);
        var months = await reports.ByPeriodAsync(scope, ReportGrouping.Month, includeProducts: true, cancellationToken);
        var branchName = scope.BranchId is { } branchId
            ? await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        var period = ReportNames.Period(scope);
        var scopeText = branchName ?? "Todas las sucursales";
        var fileName = ReportNames.FileName(scope, format);

        var appointments = summary.Appointments;
        var summaryRows = new List<object?[]>
        {
            new object?[] { "Ingreso total", summary.TotalIncome },
            new object?[] { "Ingresos por turnos", appointments.Income },
            new object?[] { "Ventas de productos", summary.Products.Income },
            new object?[] { "Costo de los productos vendidos", summary.Products.Cost },
            new object?[] { "Ganancia de productos", summary.Products.Profit },
            new object?[] { "Turnos que cuentan como ingreso", appointments.IncomeCount },
            new object?[] { "Turnos completados", appointments.Completed },
            new object?[] { "Cancelaciones tardías cobradas", appointments.LateCancellationsCharged },
            new object?[] { "Pagado por adelantado (turnos futuros)", appointments.Prepaid.Amount },
            new object?[] { "Turnos pagados por adelantado", appointments.Prepaid.Count },
            new object?[] { "Pedidos", summary.Products.Orders },
            new object?[] { "Unidades vendidas", summary.Products.Units },
        };
        var statusRows = appointments.ByStatus.Select(s => new object?[] { StatusLabel(s.Key), s.Value }).ToList();
        var monthRows = months.Select(p => new object?[] { MonthLabel(p.Period), p.AppointmentIncome, p.ProductIncome, p.TotalIncome }).ToList();
        var branchRows = branches.Select(b => new object?[]
            { b.Name, b.AppointmentIncome, b.AppointmentsCompleted, b.ProductIncome, b.ProductProfit, b.TotalIncome }).ToList();
        var barberRows = barbers.Select(b => new object?[]
            { b.Name, b.BranchName ?? "Varias", b.Income, b.Completed, b.Cancelled, b.Prepaid.Amount }).ToList();
        var serviceRows = services.Select(s => new object?[] { s.Name, s.Completed, s.Income }).ToList();
        var productRows = products.Products.Select(p => new object?[]
            { p.Name, p.Category, p.Units, p.Income, p.Cost, p.Profit, p.MarginPercent }).ToList();
        var categoryRows = products.Categories.Select(c => new object?[] { c.Name, c.Units, c.Income, c.Cost, c.Profit }).ToList();

        if (format == Xlsx)
        {
            var sheet = new SimpleXlsx("Reporte");
            sheet.Title(Title);
            sheet.Subtitle($"Período: {period}");
            sheet.Subtitle($"Alcance: {scopeText}");
            sheet.Blank();
            var summaryBlock = sheet.Section("Resumen", ["Concepto", "Valor"], summaryRows);
            var statusBlock = sheet.Section("Turnos por estado", ["Estado", "Cantidad"], statusRows);
            sheet.Section("Ingresos por mes", ["Mes", "Turnos", "Productos", "Total"], monthRows);
            var branchBlock = sheet.Section("Por sucursal",
                ["Sucursal", "Ingresos turnos", "Turnos completados", "Ventas productos", "Ganancia productos", "Ingreso total"], branchRows);
            sheet.Section("Por barbero", ["Barbero", "Sucursal", "Ingresos", "Completados", "Cancelados", "Pagado por adelantado"], barberRows);
            var serviceBlock = sheet.Section("Por servicio", ["Servicio", "Realizados", "Ingresos"], serviceRows);
            sheet.Section("Ganancia de productos", ["Producto", "Categoría", "Unidades", "Ventas", "Costo", "Ganancia", "Margen %"],
                productRows, percentColumn: 6);
            sheet.Section("Por categoría", ["Categoría", "Unidades", "Ventas", "Costo", "Ganancia"], categoryRows);
            sheet.Paragraph(IncomeNote, 7);

            sheet.PieChart("Turnos por estado", statusBlock, 0, 1);
            sheet.PieChart("Origen de los ingresos", new XlsxBlock(summaryBlock.FirstRow + 1, summaryBlock.FirstRow + 2), 0, 1);
            sheet.PieChart("Ingresos por sucursal", branchBlock, 0, 5);
            sheet.PieChart("Servicios realizados", serviceBlock, 0, 1);
            return new ExportedFile(sheet.Build(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        var pdf = new SimplePdf(Title, [$"Período: {period}", $"Alcance: {scopeText}"], $"Cráneo Barbero · Reporte {period} · {scopeText}");
        pdf.Stats(
        [
            new("Ingreso total", Money(summary.TotalIncome), "Turnos y productos"),
            new("Ingresos por turnos", Money(appointments.Income), $"{appointments.IncomeCount} turnos cobrados"),
            new("Ventas de productos", Money(summary.Products.Income), $"{summary.Products.Orders} pedidos · {summary.Products.Units} unidades"),
            new("Ganancia de productos", Money(summary.Products.Profit), $"Costo {Money(summary.Products.Cost)}"),
        ]);
        pdf.Section("Resumen", [new("Concepto", 3f), new("Valor", 1.2f, true)], Format(summaryRows));
        pdf.PieRow(
        [
            Pie("Turnos por estado", appointments.ByStatus.Select(s => (StatusLabel(s.Key), (decimal)s.Value)), Count),
            Pie("Origen de los ingresos", [("Turnos", appointments.Income), ("Productos", summary.Products.Income)], Money),
        ]);
        pdf.Section("Ingresos por mes", [new("Mes", 1.6f), new("Turnos", 1.3f, true), new("Productos", 1.3f, true), new("Total", 1.3f, true)], Format(monthRows));
        pdf.Section("Por sucursal", [new("Sucursal", 2.3f), new("Turnos", 1.2f, true), new("Completados", 1.4f, true), new("Productos", 1.2f, true),
            new("Ganancia prod.", 1.5f, true), new("Total", 1.2f, true)], Format(branchRows));
        pdf.PieRow(
        [
            Pie("Ingresos por sucursal", branches.Select(b => (b.Name, b.TotalIncome)), Money),
            Pie("Servicios realizados", services.Select(s => (s.Name, (decimal)s.Completed)), Count),
        ]);
        pdf.Section("Por barbero", [new("Barbero", 1.9f), new("Sucursal", 2.1f), new("Ingresos", 1.2f, true), new("Completados", 1.3f, true),
            new("Cancelados", 1.2f, true), new("Adelantado", 1.2f, true)], Format(barberRows));
        pdf.Section("Por servicio", [new("Servicio", 3f), new("Realizados", 1f, true), new("Ingresos", 1.3f, true)], Format(serviceRows));
        pdf.Section("Ganancia de productos", [new("Producto", 2.4f), new("Categoría", 1.6f), new("Unid.", 0.7f, true), new("Ventas", 1.2f, true),
            new("Costo", 1.2f, true), new("Ganancia", 1.2f, true), new("Margen", 0.9f, true)], Format(productRows, percentColumn: 6));
        pdf.Section("Por categoría", [new("Categoría", 2.4f), new("Unidades", 1f, true), new("Ventas", 1.3f, true), new("Costo", 1.3f, true),
            new("Ganancia", 1.3f, true)], Format(categoryRows));
        pdf.Paragraph(IncomeNote);
        return new ExportedFile(pdf.Build(), "application/pdf", fileName);
    }

    private static ChartData Pie(string title, IEnumerable<(string Label, decimal Value)> values, Func<decimal, string> display) =>
        new(title, ReportPalette.Group(values.Select(v => new ChartSlice(v.Label, v.Value, display(v.Value))).ToList(), display));

    private static string Money(decimal value) => "$" + value.ToString("#,##0.##", Spanish);

    private static string Count(decimal value) => value == 1 ? "1 turno" : $"{value.ToString("#,##0", Spanish)} turnos";

    private static string MonthLabel(string key) =>
        DateOnly.TryParseExact(key, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? Spanish.TextInfo.ToTitleCase(month.ToString("MMMM yyyy", Spanish))
            : key;

    private static List<string[]> Format(List<object?[]> rows, int? percentColumn = null) =>
        rows.Select(row => row.Select((cell, index) => cell switch
        {
            null => "-",
            decimal value when index == percentColumn => $"{value.ToString("0.##", Spanish)}%",
            decimal value => Money(value),
            int value => value.ToString("#,##0", Spanish),
            _ => cell.ToString() ?? string.Empty,
        }).ToArray()).ToList();

    private static string StatusLabel(string status) => status switch
    {
        "awaiting_payment" => "Esperando pago",
        "pending" => "Pendiente",
        "completed" => "Completado",
        "cancelled" => "Cancelado",
        "expired" => "Expirado",
        _ => status,
    };
}
