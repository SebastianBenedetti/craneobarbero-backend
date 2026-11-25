using System.Globalization;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Reports;

public sealed record ReportScope(DateOnly From, DateOnly To, DateTimeOffset Start, DateTimeOffset End, int? BranchId, int? BarberId, int? CategoryId);

internal sealed record AppointmentRow(
    int Id,
    DateTimeOffset Date,
    AppointmentStatus Status,
    PaymentStatus PaymentStatus,
    decimal Price,
    int BarberId,
    string BarberName,
    int BranchId,
    string BranchName,
    int CutId,
    string CutName,
    int? ClientId,
    string? ClientName);

internal sealed record OrderItemRow(
    int OrderId,
    DateTimeOffset Date,
    int BranchId,
    string BranchName,
    int ProductId,
    string ProductName,
    int CategoryId,
    string CategoryName,
    int Quantity,
    decimal UnitPrice,
    decimal UnitCost);

public sealed class ReportService(AppDbContext db, BusinessClock clock)
{
    public ReportScope Scope(ReportQuery query, int? barberId = null)
    {
        var today = clock.Today;
        var to = query.To ?? (query.From is { } from && from > today ? from : today);
        var start = query.From ?? new DateOnly(to.Year, to.Month, 1);
        return new ReportScope(
            start,
            to,
            clock.ToUtc(start, TimeOnly.MinValue),
            clock.ToUtc(to.AddDays(1), TimeOnly.MinValue),
            query.BranchId,
            barberId,
            query.CategoryId);
    }

    public static bool CountsAsIncome(AppointmentStatus status, PaymentStatus payment) =>
        payment == PaymentStatus.Paid && status is AppointmentStatus.Completed or AppointmentStatus.Cancelled;

    public static bool IsPrepaid(AppointmentStatus status, PaymentStatus payment) =>
        payment == PaymentStatus.Paid && status == AppointmentStatus.Pending;

    public static bool OrderCounts(OrderStatus status, RefundStatus refund) =>
        status is OrderStatus.Paid or OrderStatus.Ready or OrderStatus.Delivered ||
        (status == OrderStatus.Cancelled && refund == RefundStatus.NotRefunded);

    public async Task<AdminSummaryResponse> AdminSummaryAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        var items = await OrderItemsAsync(scope, cancellationToken);
        var appointmentSummary = Summarize(appointments);
        var products = new ProductSummary(
            items.Sum(i => i.Quantity * i.UnitPrice),
            items.Sum(i => i.Quantity * i.UnitCost),
            items.Sum(i => i.Quantity * (i.UnitPrice - i.UnitCost)),
            items.Select(i => i.OrderId).Distinct().Count(),
            items.Sum(i => i.Quantity));
        return new AdminSummaryResponse(Period(scope), scope.BranchId, appointmentSummary, products, appointmentSummary.Income + products.Income);
    }

    public async Task<IReadOnlyList<BranchReportRow>> ByBranchAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        var items = await OrderItemsAsync(scope, cancellationToken);
        var branches = await db.Branches.AsNoTracking()
            .Where(b => scope.BranchId == null || b.Id == scope.BranchId)
            .Select(b => new { b.Id, b.Name })
            .ToListAsync(cancellationToken);
        return branches
            .Select(b =>
            {
                var own = appointments.Where(a => a.BranchId == b.Id).ToList();
                var sold = items.Where(i => i.BranchId == b.Id).ToList();
                var appointmentIncome = own.Where(a => CountsAsIncome(a.Status, a.PaymentStatus)).Sum(a => a.Price);
                var productIncome = sold.Sum(i => i.Quantity * i.UnitPrice);
                return new BranchReportRow(
                    b.Id,
                    b.Name,
                    appointmentIncome,
                    own.Count(a => a.Status == AppointmentStatus.Completed),
                    productIncome,
                    sold.Sum(i => i.Quantity * (i.UnitPrice - i.UnitCost)),
                    appointmentIncome + productIncome);
            })
            .OrderByDescending(r => r.TotalIncome)
            .ThenBy(r => r.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<BarberReportRow>> ByBarberAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        return appointments
            .GroupBy(a => a.BarberId)
            .Select(g => new BarberReportRow(
                g.Key,
                g.First().BarberName,
                g.Select(a => a.BranchName).Distinct().Count() == 1 ? g.First().BranchName : null,
                g.Where(a => CountsAsIncome(a.Status, a.PaymentStatus)).Sum(a => a.Price),
                g.Count(a => a.Status == AppointmentStatus.Completed),
                g.Count(a => a.Status == AppointmentStatus.Cancelled),
                Prepaid(g)))
            .OrderByDescending(r => r.Income)
            .ThenBy(r => r.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<ServiceReportRow>> ByServiceAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        return appointments
            .GroupBy(a => a.CutId)
            .Select(g => new ServiceReportRow(
                g.Key,
                g.First().CutName,
                g.Count(a => a.Status == AppointmentStatus.Completed),
                g.Where(a => CountsAsIncome(a.Status, a.PaymentStatus)).Sum(a => a.Price)))
            .Where(r => r.Completed > 0 || r.Income > 0)
            .OrderByDescending(r => r.Completed)
            .ThenByDescending(r => r.Income)
            .ThenBy(r => r.Name)
            .ToList();
    }

    public async Task<ProductReportResponse> ByProductAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var items = await OrderItemsAsync(scope, cancellationToken);
        var products = items
            .GroupBy(i => i.ProductId)
            .Select(g =>
            {
                var income = g.Sum(i => i.Quantity * i.UnitPrice);
                var cost = g.Sum(i => i.Quantity * i.UnitCost);
                return new ProductReportRow(
                    g.Key,
                    g.First().ProductName,
                    g.First().CategoryName,
                    g.Sum(i => i.Quantity),
                    income,
                    cost,
                    income - cost,
                    income > 0 ? decimal.Round((income - cost) / income * 100m, 2) : null);
            })
            .OrderByDescending(r => r.Profit)
            .ThenBy(r => r.Name)
            .ToList();
        var categories = items
            .GroupBy(i => i.CategoryId)
            .Select(g => new CategoryReportRow(
                g.Key,
                g.First().CategoryName,
                g.Sum(i => i.Quantity),
                g.Sum(i => i.Quantity * i.UnitPrice),
                g.Sum(i => i.Quantity * i.UnitCost),
                g.Sum(i => i.Quantity * (i.UnitPrice - i.UnitCost))))
            .OrderByDescending(r => r.Profit)
            .ThenBy(r => r.Name)
            .ToList();
        return new ProductReportResponse(Period(scope), products, categories);
    }

    public async Task<IReadOnlyList<PeriodReportRow>> ByPeriodAsync(ReportScope scope, string? groupBy, bool includeProducts, CancellationToken cancellationToken)
    {
        var monthly = groupBy == ReportGrouping.Month || (groupBy is null && scope.To.DayNumber - scope.From.DayNumber > 62);
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        var items = includeProducts ? await OrderItemsAsync(scope, cancellationToken) : [];

        var keys = new List<string>();
        for (var day = scope.From; day <= scope.To; day = day.AddDays(1))
        {
            var key = Key(day, monthly);
            if (keys.Count == 0 || keys[^1] != key)
            {
                keys.Add(key);
            }
        }

        var appointmentIncome = appointments
            .Where(a => CountsAsIncome(a.Status, a.PaymentStatus))
            .GroupBy(a => Key(LocalDay(a.Date), monthly))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Price));
        var productIncome = items
            .GroupBy(i => Key(LocalDay(i.Date), monthly))
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity * i.UnitPrice));
        return keys
            .Select(key =>
            {
                var appointmentsTotal = appointmentIncome.GetValueOrDefault(key);
                var productsTotal = productIncome.GetValueOrDefault(key);
                return new PeriodReportRow(key, appointmentsTotal, productsTotal, appointmentsTotal + productsTotal);
            })
            .ToList();
    }

    public async Task<BarberSummaryResponse> BarberSummaryAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var summary = Summarize(await AppointmentsAsync(scope, cancellationToken));
        return new BarberSummaryResponse(Period(scope), summary.Income, summary.IncomeCount, summary.Completed, summary.Prepaid, summary.ByStatus);
    }

    public async Task<IReadOnlyList<RegularClientRow>> RegularClientsAsync(ReportScope scope, int limit, CancellationToken cancellationToken)
    {
        var appointments = await AppointmentsAsync(scope, cancellationToken);
        return appointments
            .Where(a => a.Status == AppointmentStatus.Completed && (a.ClientId is not null || !string.IsNullOrWhiteSpace(a.ClientName)))
            .GroupBy(a => a.ClientId is { } id ? $"id:{id}" : $"name:{a.ClientName!.Trim().ToLowerInvariant()}")
            .Select(g => new RegularClientRow(
                g.First().ClientId,
                g.OrderByDescending(a => a.Date).First().ClientName ?? "Cliente",
                g.Count(),
                g.Max(a => a.Date)))
            .OrderByDescending(r => r.Completed)
            .ThenByDescending(r => r.LastAppointmentDate)
            .Take(limit)
            .ToList();
    }

    internal async Task<List<AppointmentRow>> AppointmentsAsync(ReportScope scope, CancellationToken cancellationToken) =>
        await db.Appointments.AsNoTracking()
            .Where(a => a.AppointmentDate >= scope.Start && a.AppointmentDate < scope.End)
            .Where(a => scope.BranchId == null || a.BranchId == scope.BranchId)
            .Where(a => scope.BarberId == null || a.BarberId == scope.BarberId)
            .Select(a => new AppointmentRow(
                a.Id,
                a.AppointmentDate,
                a.Status,
                a.PaymentStatus,
                a.Price,
                a.BarberId,
                a.Barber!.Name,
                a.BranchId,
                a.Branch!.Name,
                a.CutId,
                a.Cut!.Name,
                a.ClientId,
                a.Client != null ? a.Client.Name : a.ClientName))
            .ToListAsync(cancellationToken);

    internal async Task<List<OrderItemRow>> OrderItemsAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var rows = await db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.CreatedAt >= scope.Start && i.Order.CreatedAt < scope.End)
            .Where(i => scope.BranchId == null || i.Order!.BranchId == scope.BranchId)
            .Where(i => scope.CategoryId == null || i.Product!.CategoryId == scope.CategoryId)
            .Select(i => new
            {
                i.OrderId,
                i.Order!.CreatedAt,
                i.Order.Status,
                i.Order.RefundStatus,
                i.Order.BranchId,
                BranchName = i.Order.Branch!.Name,
                i.ProductId,
                ProductName = i.Product!.Name,
                i.Product.CategoryId,
                CategoryName = i.Product.Category!.Name,
                i.Quantity,
                i.UnitPrice,
                i.UnitCost,
            })
            .ToListAsync(cancellationToken);
        return rows
            .Where(r => OrderCounts(r.Status, r.RefundStatus))
            .Select(r => new OrderItemRow(r.OrderId, r.CreatedAt, r.BranchId, r.BranchName, r.ProductId, r.ProductName,
                r.CategoryId, r.CategoryName, r.Quantity, r.UnitPrice, r.UnitCost ?? 0m))
            .ToList();
    }

    private static AppointmentSummary Summarize(List<AppointmentRow> appointments)
    {
        var income = appointments.Where(a => CountsAsIncome(a.Status, a.PaymentStatus)).ToList();
        var byStatus = Enum.GetValues<AppointmentStatus>()
            .ToDictionary(StatusNames.Of, status => appointments.Count(a => a.Status == status));
        return new AppointmentSummary(
            income.Sum(a => a.Price),
            income.Count,
            appointments.Count(a => a.Status == AppointmentStatus.Completed),
            income.Count(a => a.Status == AppointmentStatus.Cancelled),
            Prepaid(appointments),
            byStatus);
    }

    private static PrepaidResponse Prepaid(IEnumerable<AppointmentRow> appointments)
    {
        var prepaid = appointments.Where(a => IsPrepaid(a.Status, a.PaymentStatus)).ToList();
        return new PrepaidResponse(prepaid.Count, prepaid.Sum(a => a.Price));
    }

    private DateOnly LocalDay(DateTimeOffset instant) => DateOnly.FromDateTime(clock.ToBusinessTime(instant).DateTime);

    private static string Key(DateOnly day, bool monthly) =>
        monthly ? day.ToString("yyyy-MM", CultureInfo.InvariantCulture) : day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ReportPeriodResponse Period(ReportScope scope) => new(scope.From, scope.To);
}
