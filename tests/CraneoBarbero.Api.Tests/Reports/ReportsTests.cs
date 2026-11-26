using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using CraneoBarbero.Api.Tests.Store;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Reports;

public sealed class ReportsTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private const string November = "from=2026-11-01&to=2026-11-30";
    private static Seed? _seed;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);

    public static TheoryData<string> AdminEndpoints => new()
    {
        "/reports/admin/summary", "/reports/admin/branches", "/reports/admin/barbers", "/reports/admin/services",
        "/reports/admin/products", "/reports/admin/periods", "/reports/admin/export?format=pdf",
    };

    public static TheoryData<string> BarberEndpoints => new()
    {
        "/reports/barber/summary", "/reports/barber/services", "/reports/barber/clients", "/reports/barber/periods",
    };

    [Theory]
    [MemberData(nameof(AdminEndpoints))]
    public async Task Admin_reports_are_only_for_the_admin(string url)
    {
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Theory]
    [MemberData(nameof(BarberEndpoints))]
    public async Task Barber_reports_are_only_for_barbers(string url)
    {
        foreach (var role in new[] { UserRole.Client, UserRole.Admin })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Summary_counts_income_with_the_business_rules()
    {
        var s = await SeedAsync();

        var summary = await s.Admin.GetFromJsonAsync<JsonElement>($"/reports/admin/summary?{November}");

        var appointments = summary.GetProperty("appointments");
        Assert.Equal(6000m, appointments.GetProperty("income").GetDecimal());
        Assert.Equal(4, appointments.GetProperty("incomeCount").GetInt32());
        Assert.Equal(3, appointments.GetProperty("completed").GetInt32());
        Assert.Equal(1, appointments.GetProperty("lateCancellationsCharged").GetInt32());
        Assert.Equal(1000m, appointments.GetProperty("prepaid").GetProperty("amount").GetDecimal());
        Assert.Equal(2, appointments.GetProperty("byStatus").GetProperty("cancelled").GetInt32());
        Assert.Equal(1, appointments.GetProperty("byStatus").GetProperty("expired").GetInt32());
        var products = summary.GetProperty("products");
        Assert.Equal(2500m, products.GetProperty("income").GetDecimal());
        Assert.Equal(1300m, products.GetProperty("cost").GetDecimal());
        Assert.Equal(1200m, products.GetProperty("profit").GetDecimal());
        Assert.Equal(3, products.GetProperty("orders").GetInt32());
        Assert.Equal(4, products.GetProperty("units").GetInt32());
        Assert.Equal(8500m, summary.GetProperty("totalIncome").GetDecimal());

        var branchOnly = await s.Admin.GetFromJsonAsync<JsonElement>($"/reports/admin/summary?{November}&branchId={s.BranchB}");
        Assert.Equal(2000m, branchOnly.GetProperty("appointments").GetProperty("income").GetDecimal());
        Assert.Equal(1000m, branchOnly.GetProperty("products").GetProperty("income").GetDecimal());
    }

    [Fact]
    public async Task Reports_group_by_branch_barber_service_product_and_period()
    {
        var s = await SeedAsync();

        var branches = await s.Admin.GetFromJsonAsync<List<JsonElement>>($"/reports/admin/branches?{November}");
        var branchA = branches!.Single(b => b.GetProperty("branchId").GetInt32() == s.BranchA);
        Assert.Equal(4000m, branchA.GetProperty("appointmentIncome").GetDecimal());
        Assert.Equal(1500m, branchA.GetProperty("productIncome").GetDecimal());
        Assert.Equal(600m, branchA.GetProperty("productProfit").GetDecimal());
        Assert.Equal(5500m, branchA.GetProperty("totalIncome").GetDecimal());

        var barbers = await s.Admin.GetFromJsonAsync<List<JsonElement>>($"/reports/admin/barbers?{November}");
        var first = barbers!.Single(b => b.GetProperty("barberId").GetInt32() == s.BarberA.Id);
        Assert.Equal(4000m, first.GetProperty("income").GetDecimal());
        Assert.Equal(2, first.GetProperty("completed").GetInt32());
        Assert.Equal(2, first.GetProperty("cancelled").GetInt32());
        Assert.Equal(1000m, first.GetProperty("prepaid").GetProperty("amount").GetDecimal());

        var services = await s.Admin.GetFromJsonAsync<List<JsonElement>>($"/reports/admin/services?{November}");
        Assert.Equal(s.CutX, services![0].GetProperty("cutId").GetInt32());
        Assert.Equal(2, services[0].GetProperty("completed").GetInt32());
        Assert.Equal(4000m, services.Single(r => r.GetProperty("cutId").GetInt32() == s.CutY).GetProperty("income").GetDecimal());

        var products = await s.Admin.GetFromJsonAsync<JsonElement>($"/reports/admin/products?{November}");
        var rows = products.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal("Cera reporte", rows[0].GetProperty("name").GetString());
        Assert.Equal(3, rows[0].GetProperty("units").GetInt32());
        Assert.Equal(600m, rows[0].GetProperty("profit").GetDecimal());
        Assert.Equal(40m, rows[0].GetProperty("marginPercent").GetDecimal());
        Assert.Equal(60m, rows[1].GetProperty("marginPercent").GetDecimal());
        Assert.Equal(2, products.GetProperty("categories").GetArrayLength());

        var monthly = await s.Admin.GetFromJsonAsync<List<JsonElement>>($"/reports/admin/periods?{November}&groupBy=month");
        var row = Assert.Single(monthly!);
        Assert.Equal("2026-11", row.GetProperty("period").GetString());
        Assert.Equal(8500m, row.GetProperty("totalIncome").GetDecimal());
        var daily = await s.Admin.GetFromJsonAsync<List<JsonElement>>($"/reports/admin/periods?{November}&groupBy=day");
        Assert.Equal(30, daily!.Count);
        Assert.Equal(1000m, daily.Single(d => d.GetProperty("period").GetString() == "2026-11-05").GetProperty("appointmentIncome").GetDecimal());
    }

    [Fact]
    public async Task Barbers_only_see_their_own_numbers_and_regular_clients()
    {
        var s = await SeedAsync();
        var barber = factory.ClientWithToken(factory.TokenFor(s.BarberA));

        var summary = await barber.GetFromJsonAsync<JsonElement>($"/reports/barber/summary?{November}&branchId={s.BranchB}");
        Assert.Equal(4000m, summary.GetProperty("income").GetDecimal());
        Assert.Equal(2, summary.GetProperty("completed").GetInt32());
        Assert.False(summary.TryGetProperty("branchId", out _));

        var clients = await barber.GetFromJsonAsync<List<JsonElement>>($"/reports/barber/clients?{November}");
        var top = Assert.Single(clients!);
        Assert.Equal(s.Client.Id, top.GetProperty("clientId").GetInt32());
        Assert.Equal(2, top.GetProperty("completed").GetInt32());

        var other = factory.ClientWithToken(factory.TokenFor(s.BarberB));
        var otherClients = await other.GetFromJsonAsync<List<JsonElement>>($"/reports/barber/clients?{November}");
        Assert.Equal("Cliente sin cuenta", Assert.Single(otherClients!).GetProperty("name").GetString());
        var services = await other.GetFromJsonAsync<List<JsonElement>>($"/reports/barber/services?{November}");
        Assert.Equal(s.CutY, Assert.Single(services!).GetProperty("cutId").GetInt32());
    }

    [Fact]
    public async Task Reports_export_to_pdf_and_excel()
    {
        var s = await SeedAsync();

        var pdf = await s.Admin.GetAsync($"/reports/admin/export?format=pdf&{November}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Reporte 2026-11 - Craneo Barbero.pdf", pdf.Content.Headers.ContentDisposition!.FileNameStar ?? pdf.Content.Headers.ContentDisposition!.FileName?.Trim('"'));
        var pdfBytes = await pdf.Content.ReadAsByteArrayAsync();
        var pdfText = System.Text.Encoding.Latin1.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.4", pdfText);
        Assert.Contains("Ganancia de productos", pdfText);
        Assert.Contains("Cera reporte", pdfText);
        Assert.Contains("Noviembre 2026", pdfText);
        Assert.Contains("Turnos por estado", pdfText);
        Assert.EndsWith("%%EOF\n", pdfText);

        var xlsx = await s.Admin.GetAsync($"/reports/admin/export?format=xlsx&{November}");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()));
        using var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("name=\"Reporte\"", await workbook.ReadToEndAsync());
        Assert.Single(zip.Entries, e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal));
        Assert.Equal(4, zip.Entries.Count(e => e.FullName.StartsWith("xl/charts/chart", StringComparison.Ordinal)));
        using var reportSheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheetXml = await reportSheet.ReadToEndAsync();
        Assert.Contains("<v>8500</v>", sheetXml);
        Assert.Contains("Ganancia de productos", sheetXml);
        Assert.Contains("Noviembre 2026", sheetXml);

        await HttpAssertions.AssertErrorAsync(await s.Admin.GetAsync($"/reports/admin/export?format=doc&{November}"), HttpStatusCode.BadRequest, "bad_request");
        await HttpAssertions.AssertErrorAsync(await s.Admin.GetAsync("/reports/admin/summary?from=2026-11-30&to=2026-11-01"), HttpStatusCode.BadRequest, "validation_error");
    }

    private sealed record Seed(
        HttpClient Admin,
        int BranchA,
        int BranchB,
        User BarberA,
        User BarberB,
        User Client,
        int CutX,
        int CutY);

    private async Task<Seed> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (_seed is not null)
            {
                return _seed with { Admin = await factory.ClientForAsync(UserRole.Admin) };
            }

            var (branchA, barberA) = await factory.AddBarberAsync();
            var (branchB, barberB) = await factory.AddBarberAsync();
            var client = await factory.AddUserAsync(UserRole.Client);
            var wax = await factory.AddProductAsync(p => { p.Name = "Cera reporte"; p.SalePrice = 500m; p.Cost = 300m; });
            var gel = await factory.AddProductAsync(p => { p.Name = "Gel reporte"; p.SalePrice = 1000m; p.Cost = 400m; });
            var cuts = await factory.WithDbAsync(async db =>
            {
                var x = new Cut { Name = "Corte reporte", Price = 1000m, DurationMinutes = 30 };
                var y = new Cut { Name = "Barba reporte", Price = 2000m, DurationMinutes = 30 };
                db.Cuts.AddRange(x, y);
                await db.SaveChangesAsync();
                return (X: x.Id, Y: y.Id);
            });

            await factory.WithDbAsync(async db =>
            {
                Appointment Add(User barber, Branch branch, int cutId, decimal price, DateTimeOffset at, AppointmentStatus status, PaymentStatus payment, int? clientId = null, string? clientName = null)
                {
                    var appointment = new Appointment
                    {
                        BarberId = barber.Id,
                        BranchId = branch.Id,
                        CutId = cutId,
                        Price = price,
                        AppointmentDate = at,
                        EndDate = at.AddMinutes(30),
                        Status = status,
                        PaymentStatus = payment,
                        ClientId = clientId,
                        ClientName = clientName,
                    };
                    db.Appointments.Add(appointment);
                    return appointment;
                }

                Add(barberA, branchA, cuts.X, 1000m, Local(2026, 11, 5, 10), AppointmentStatus.Completed, PaymentStatus.Paid, client.Id);
                Add(barberA, branchA, cuts.X, 1000m, Local(2026, 11, 12, 10), AppointmentStatus.Completed, PaymentStatus.Paid, client.Id);
                Add(barberA, branchA, cuts.Y, 2000m, Local(2026, 11, 14, 10), AppointmentStatus.Cancelled, PaymentStatus.Paid, client.Id);
                Add(barberA, branchA, cuts.Y, 2000m, Local(2026, 11, 15, 10), AppointmentStatus.Cancelled, PaymentStatus.Refunded, client.Id);
                Add(barberA, branchA, cuts.X, 1000m, Local(2026, 11, 28, 10), AppointmentStatus.Pending, PaymentStatus.Paid, client.Id);
                Add(barberA, branchA, cuts.X, 1000m, Local(2026, 11, 20, 10), AppointmentStatus.Expired, PaymentStatus.Unpaid, client.Id);
                Add(barberB, branchB, cuts.Y, 2000m, Local(2026, 11, 10, 15), AppointmentStatus.Completed, PaymentStatus.Paid, clientName: "Cliente sin cuenta");
                Add(barberA, branchA, cuts.X, 1000m, Local(2026, 10, 20, 10), AppointmentStatus.Completed, PaymentStatus.Paid, client.Id);

                Order AddOrder(Branch branch, OrderStatus status, RefundStatus refund, DateTimeOffset at, Product product, int quantity)
                {
                    var order = new Order
                    {
                        ClientId = client.Id,
                        BranchId = branch.Id,
                        Status = status,
                        RefundStatus = refund,
                        Total = product.SalePrice * quantity,
                        CreatedAt = at,
                        UpdatedAt = at,
                        Items = [new OrderItem { ProductId = product.Id, Quantity = quantity, UnitPrice = product.SalePrice, UnitCost = product.Cost }],
                    };
                    db.Orders.Add(order);
                    return order;
                }

                AddOrder(branchA, OrderStatus.Delivered, RefundStatus.None, Local(2026, 11, 6, 12), wax, 2);
                AddOrder(branchA, OrderStatus.Cancelled, RefundStatus.NotRefunded, Local(2026, 11, 7, 12), wax, 1);
                AddOrder(branchA, OrderStatus.Cancelled, RefundStatus.Refunded, Local(2026, 11, 8, 12), wax, 1);
                AddOrder(branchA, OrderStatus.PendingPayment, RefundStatus.None, Local(2026, 11, 9, 12), wax, 1);
                AddOrder(branchB, OrderStatus.Paid, RefundStatus.None, Local(2026, 11, 10, 12), gel, 1);
                return await db.SaveChangesAsync();
            });

            _seed = new Seed(await factory.ClientForAsync(UserRole.Admin), branchA.Id, branchB.Id, barberA, barberB, client, cuts.X, cuts.Y);
            return _seed;
        }
        finally
        {
            SeedLock.Release();
        }
    }
}
