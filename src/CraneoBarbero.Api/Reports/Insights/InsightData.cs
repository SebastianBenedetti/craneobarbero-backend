using CraneoBarbero.Api.Assistant;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Schedule;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Reports.Insights;

public sealed record OccupancyRow(string Label, int OpenSlots, int BusySlots)
{
    public decimal? Percent => OpenSlots == 0 ? null : Math.Round(BusySlots * 100m / OpenSlots, 1);
}

public sealed record BarberInsight(string Name, string? BranchName, int Completed, decimal Income, decimal? Occupancy, double? Rating);

public sealed record ReviewInsight(int Count, double? AverageBarber, double? AverageCut, IReadOnlyList<string> Comments);

public sealed record InsightSnapshot(
    ReportScope Scope,
    string ScopeName,
    AdminSummaryResponse Summary,
    IReadOnlyList<PeriodReportRow> Months,
    IReadOnlyList<BranchReportRow> Branches,
    IReadOnlyList<ServiceReportRow> Services,
    ProductReportResponse Products,
    OccupancyRow Occupancy,
    IReadOnlyList<OccupancyRow> OccupancyByBranch,
    IReadOnlyList<OccupancyRow> OccupancyByWeekday,
    IReadOnlyList<OccupancyRow> OccupancyByPart,
    IReadOnlyList<(int Hour, int Count)> BusiestHours,
    IReadOnlyList<BarberInsight> Barbers,
    ReviewInsight Reviews);

public sealed class InsightData(AppDbContext db, ReportService reports, BusinessClock clock, OpeningSchedule opening)
{
    public const int MaxComments = 40;
    public const int MaxCommentLength = 200;

    private static readonly string[] Weekdays = ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];

    private static readonly AppointmentStatus[] Occupied = [AppointmentStatus.Pending, AppointmentStatus.Completed];

    public async Task<InsightSnapshot> BuildAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        var summary = await reports.AdminSummaryAsync(scope, cancellationToken);
        var months = await reports.ByPeriodAsync(scope, ReportGrouping.Month, includeProducts: true, cancellationToken);
        var branches = await reports.ByBranchAsync(scope, cancellationToken);
        var services = await reports.ByServiceAsync(scope, cancellationToken);
        var products = await reports.ByProductAsync(scope, cancellationToken);
        var barberRows = await reports.ByBarberAsync(scope, cancellationToken);

        var scopeName = scope.BranchId is { } branchId
            ? await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken) ?? "Sucursal"
            : "Todas las sucursales";

        var barbers = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.BranchId != null && (scope.BranchId == null || u.BranchId == scope.BranchId))
            .Select(u => new { u.Id, u.Name, BranchId = u.BranchId!.Value, BranchName = u.Branch!.Name })
            .ToListAsync(cancellationToken);
        var barberIds = barbers.Select(b => b.Id).ToList();
        var branchOf = barbers.ToDictionary(b => b.Id, b => b.BranchName);

        var end = scope.End < clock.UtcNow ? scope.End : clock.UtcNow;
        var slots = await db.AvailableSlots.AsNoTracking()
            .Where(s => s.DeletedAt == null && s.StartAt >= scope.Start && s.StartAt < end && barberIds.Contains(s.BarberId))
            .Select(s => new { s.BarberId, s.StartAt })
            .ToListAsync(cancellationToken);
        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => Occupied.Contains(a.Status) && a.AppointmentDate >= scope.Start && a.AppointmentDate < end &&
                        barberIds.Contains(a.BarberId))
            .Select(a => new { a.BarberId, a.AppointmentDate, a.EndDate })
            .ToListAsync(cancellationToken);

        var slotMinutes = opening.SlotMinutes;
        var busy = appointments.Select(a => new
        {
            a.BarberId,
            Local = clock.ToBusinessTime(a.AppointmentDate),
            Blocks = Math.Max(1, (int)Math.Ceiling((a.EndDate - a.AppointmentDate).TotalMinutes / slotMinutes)),
        }).ToList();
        var open = slots.Select(s => new { s.BarberId, Local = clock.ToBusinessTime(s.StartAt) }).ToList();

        OccupancyRow Row(string label, Func<int, DateTimeOffset, bool> filter) => new(
            label,
            open.Count(s => filter(s.BarberId, s.Local)),
            busy.Where(a => filter(a.BarberId, a.Local)).Sum(a => a.Blocks));

        var total = Row("Total", (_, _) => true);
        var byBranch = barbers.Select(b => b.BranchName).Distinct().OrderBy(n => n)
            .Select(name => Row(name, (barber, _) => branchOf[barber] == name))
            .Where(r => r.OpenSlots > 0)
            .ToList();
        var byWeekday = Enumerable.Range(1, 6).Append(0)
            .Select(day => Row(Weekdays[day], (_, local) => (int)local.DayOfWeek == day))
            .Where(r => r.OpenSlots > 0)
            .ToList();
        var byPart = new[]
        {
            Row("Mañana (hasta las 13:30)", (_, local) => DayParts.Of(TimeOnly.FromDateTime(local.DateTime)) == DayPart.Morning),
            Row("Tarde (desde las 14:00)", (_, local) => DayParts.Of(TimeOnly.FromDateTime(local.DateTime)) == DayPart.Afternoon),
        }.Where(r => r.OpenSlots > 0).ToList();
        var busiestHours = busy.GroupBy(a => a.Local.Hour)
            .Select(g => (Hour: g.Key, Count: g.Count()))
            .OrderByDescending(h => h.Count).ThenBy(h => h.Hour)
            .Take(3)
            .ToList();

        var reviewRows = await db.Reviews.AsNoTracking()
            .Where(r => r.CreatedAt >= scope.Start && r.CreatedAt < scope.End &&
                        (scope.BranchId == null || r.Appointment!.BranchId == scope.BranchId))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.RatingBarber, r.RatingCut, r.Comment, r.Appointment!.BarberId })
            .ToListAsync(cancellationToken);
        var comments = reviewRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Comment))
            .Select(r => r.Comment!.Trim().ReplaceLineEndings(" "))
            .Select(c => c.Length > MaxCommentLength ? c[..MaxCommentLength] : c)
            .Take(MaxComments)
            .ToList();
        var reviews = new ReviewInsight(
            reviewRows.Count,
            reviewRows.Count == 0 ? null : Math.Round(reviewRows.Average(r => (double)r.RatingBarber), 2),
            reviewRows.Count == 0 ? null : Math.Round(reviewRows.Average(r => (double)r.RatingCut), 2),
            comments);

        var barberInsights = barberRows.Select(row =>
        {
            var barberBusy = busy.Where(a => a.BarberId == row.BarberId).Sum(a => a.Blocks);
            var barberOpen = open.Count(s => s.BarberId == row.BarberId);
            var ratings = reviewRows.Where(r => r.BarberId == row.BarberId).ToList();
            return new BarberInsight(
                row.Name,
                row.BranchName,
                row.Completed,
                row.Income,
                barberOpen == 0 ? null : Math.Round(barberBusy * 100m / barberOpen, 1),
                ratings.Count == 0 ? null : Math.Round(ratings.Average(r => (double)r.RatingBarber), 2));
        }).ToList();

        return new InsightSnapshot(scope, scopeName, summary, months, branches, services, products, total, byBranch, byWeekday, byPart,
            busiestHours, barberInsights, reviews);
    }
}
