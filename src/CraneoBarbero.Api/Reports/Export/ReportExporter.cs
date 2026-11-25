using System.Globalization;
using CraneoBarbero.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record ExportedFile(byte[] Content, string ContentType, string FileName);

public sealed class ReportExporter(ReportService reports, AppDbContext db)
{
    public const string Pdf = "pdf";
    public const string Xlsx = "xlsx";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public async Task<ExportedFile> ExportAsync(ReportScope scope, string format, CancellationToken cancellationToken)
    {
        var summary = await reports.AdminSummaryAsync(scope, cancellationToken);
        var branches = await reports.ByBranchAsync(scope, cancellationToken);
        var barbers = await reports.ByBarberAsync(scope, cancellationToken);
        var services = await reports.ByServiceAsync(scope, cancellationToken);
        var products = await reports.ByProductAsync(scope, cancellationToken);
        var periods = await reports.ByPeriodAsync(scope, null, includeProducts: true, cancellationToken);
        var branchName = scope.BranchId is { } branchId
            ? await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        var period = $"{scope.From.ToString("dd/MM/yyyy", Spanish)} al {scope.To.ToString("dd/MM/yyyy", Spanish)}";
        var scopeText = branchName is null ? "Todas las sucursales" : branchName;
        var fileName = $"reporte-craneo-barbero-{scope.From:yyyy-MM-dd}-a-{scope.To:yyyy-MM-dd}.{format}";

        var summaryRows = new List<object?[]>
        {
            new object?[] { "Ingresos por turnos", summary.Appointments.Income },
            new object?[] { "Turnos que cuentan como ingreso", summary.Appointments.IncomeCount },
            new object?[] { "Turnos completados", summary.Appointments.Completed },
            new object?[] { "Cancelaciones tardías cobradas", summary.Appointments.LateCancellationsCharged },
            new object?[] { "Pagados por adelantado (turnos futuros)", summary.Appointments.Prepaid.Amount },
            new object?[] { "Cantidad pagada por adelantado", summary.Appointments.Prepaid.Count },
            new object?[] { "Ventas de productos", summary.Products.Income },
            new object?[] { "Costo de los productos vendidos", summary.Products.Cost },
            new object?[] { "Ganancia de productos", summary.Products.Profit },
            new object?[] { "Pedidos", summary.Products.Orders },
            new object?[] { "Unidades vendidas", summary.Products.Units },
            new object?[] { "Ingreso total", summary.TotalIncome },
        };
        var statusRows = summary.Appointments.ByStatus
            .Select(s => new object?[] { StatusLabel(s.Key), s.Value })
            .ToList();
        var branchRows = branches.Select(b => new object?[]
            { b.Name, b.AppointmentIncome, b.AppointmentsCompleted, b.ProductIncome, b.ProductProfit, b.TotalIncome }).ToList();
        var barberRows = barbers.Select(b => new object?[]
            { b.Name, b.BranchName ?? "Varias", b.Income, b.Completed, b.Cancelled, b.Prepaid.Amount }).ToList();
        var serviceRows = services.Select(s => new object?[] { s.Name, s.Completed, s.Income }).ToList();
        var productRows = products.Products.Select(p => new object?[]
            { p.Name, p.Category, p.Units, p.Income, p.Cost, p.Profit, p.MarginPercent }).ToList();
        var categoryRows = products.Categories.Select(c => new object?[] { c.Name, c.Units, c.Income, c.Cost, c.Profit }).ToList();
        var periodRows = periods.Select(p => new object?[] { p.Period, p.AppointmentIncome, p.ProductIncome, p.TotalIncome }).ToList();

        if (format == Xlsx)
        {
            var sheets = new List<XlsxSheet>
            {
                new("Resumen", ["Concepto", "Valor"], [new object?[] { "Período", period }, new object?[] { "Alcance", scopeText }, .. summaryRows]),
                new("Turnos por estado", ["Estado", "Cantidad"], statusRows),
                new("Por sucursal", ["Sucursal", "Ingresos turnos", "Turnos completados", "Ventas productos", "Ganancia productos", "Ingreso total"], branchRows),
                new("Por barbero", ["Barbero", "Sucursal", "Ingresos", "Completados", "Cancelados", "Pagado por adelantado"], barberRows),
                new("Por servicio", ["Servicio", "Realizados", "Ingresos"], serviceRows),
                new("Por producto", ["Producto", "Categoría", "Unidades", "Ventas", "Costo", "Ganancia", "Margen %"], productRows),
                new("Por categoría", ["Categoría", "Unidades", "Ventas", "Costo", "Ganancia"], categoryRows),
                new("Por período", ["Período", "Turnos", "Productos", "Total"], periodRows),
            };
            return new ExportedFile(SimpleXlsx.Build(sheets),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        var pdf = new SimplePdf("Reporte de Cráneo Barbero", [$"Período: {period}", $"Alcance: {scopeText}"]);
        pdf.Heading("Resumen");
        pdf.Table([new("Concepto", 3f), new("Valor", 1.2f, true)], Format(summaryRows));
        pdf.Heading("Turnos por estado");
        pdf.Table([new("Estado", 3f), new("Cantidad", 1.2f, true)], Format(statusRows));
        pdf.Heading("Por sucursal");
        pdf.Table([new("Sucursal", 2.4f), new("Turnos $", 1.3f, true), new("Completados", 1.1f, true), new("Productos $", 1.3f, true),
            new("Ganancia prod.", 1.3f, true), new("Total $", 1.3f, true)], Format(branchRows));
        pdf.Heading("Por barbero");
        pdf.Table([new("Barbero", 2.2f), new("Sucursal", 1.8f), new("Ingresos", 1.3f, true), new("Completados", 1.1f, true),
            new("Cancelados", 1f, true), new("Adelantado", 1.3f, true)], Format(barberRows));
        pdf.Heading("Por servicio");
        pdf.Table([new("Servicio", 3f), new("Realizados", 1f, true), new("Ingresos", 1.3f, true)], Format(serviceRows));
        pdf.Heading("Ganancia de productos");
        pdf.Table([new("Producto", 2.4f), new("Categoría", 1.6f), new("Unid.", 0.7f, true), new("Ventas", 1.2f, true),
            new("Costo", 1.2f, true), new("Ganancia", 1.2f, true), new("Margen", 0.9f, true)], Format(productRows, percentColumn: 6));
        pdf.Heading("Por categoría");
        pdf.Table([new("Categoría", 2.4f), new("Unidades", 1f, true), new("Ventas", 1.3f, true), new("Costo", 1.3f, true),
            new("Ganancia", 1.3f, true)], Format(categoryRows));
        pdf.Heading("Ingresos por período");
        pdf.Table([new("Período", 1.5f), new("Turnos", 1.3f, true), new("Productos", 1.3f, true), new("Total", 1.3f, true)], Format(periodRows));
        pdf.Paragraph("Ingresos según RN-51: turnos pagados completados o cancelados sin reembolso; pedidos pagados, listos, entregados o cancelados sin reembolso.");
        return new ExportedFile(pdf.Build(), "application/pdf", fileName);
    }

    private static List<string[]> Format(List<object?[]> rows, int? percentColumn = null) =>
        rows.Select(row => row.Select((cell, index) => cell switch
        {
            null => "-",
            decimal value when index == percentColumn => $"{value.ToString("0.##", Spanish)}%",
            decimal value => "$" + value.ToString("#,##0.##", Spanish),
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
