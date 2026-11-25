using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Pricing;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using CraneoBarbero.Api.Tests.Store;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CraneoBarbero.Api.Tests.Schedule.ScheduleApiFactory;

namespace CraneoBarbero.Api.Tests.Pricing;

public sealed class FakeInflationSource(string name) : IInflationSource
{
    public string Name => name;
    public bool Fail { get; set; }
    public Dictionary<DateOnly, decimal> Rates { get; } = [];
    public int Calls { get; private set; }

    public Task<IReadOnlyList<MonthlyInflation>> GetMonthlyAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken)
    {
        Calls++;
        if (Fail)
        {
            throw new HttpRequestException("La API no responde.");
        }

        IReadOnlyList<MonthlyInflation> result = Rates
            .Where(r => r.Key >= fromMonth && r.Key <= toMonth)
            .OrderBy(r => r.Key)
            .Select(r => new MonthlyInflation(r.Key, r.Value))
            .ToList();
        return Task.FromResult(result);
    }
}

public sealed class PricingApiFactory : ScheduleApiFactory
{
    public FakeInflationSource Indec { get; } = new("indec_api");
    public FakeInflationSource Fallback { get; } = new("argentinadatos");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IInflationSource>();
            services.AddSingleton<IInflationSource>(Indec);
            services.AddSingleton<IInflationSource>(Fallback);
        });
    }

    public void ResetInflation()
    {
        ((MemoryCache)Services.GetRequiredService<IMemoryCache>()).Clear();
        foreach (var source in new[] { Indec, Fallback })
        {
            source.Fail = false;
            source.Rates.Clear();
        }
    }
}

public sealed class PricingTests(PricingApiFactory factory) : IClassFixture<PricingApiFactory>
{
    private static readonly DateOnly September = new(2026, 9, 1);
    private static readonly DateOnly October = new(2026, 10, 1);

    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/prices/inflation-suggestion" },
        { "POST", "/prices/adjustments/preview" },
        { "POST", "/prices/adjustments" },
        { "GET", "/prices/adjustments" },
        { "POST", "/prices/adjustments/1/revert" },
        { "GET", "/prices/history" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Prices_are_managed_only_by_the_admin(string method, string url)
    {
        var request = () => new HttpRequestMessage(new HttpMethod(method), url) { Content = method == "GET" ? null : JsonContent.Create(new { }) };
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().SendAsync(request()), HttpStatusCode.Unauthorized, "unauthorized");
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.SendAsync(request()), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Suggestion_accumulates_the_official_monthly_inflation()
    {
        factory.ResetInflation();
        factory.Indec.Rates[September] = 1.9m;
        factory.Indec.Rates[October] = 2.2m;
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var range = await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion?from=2026-09&to=2026-10");
        var latest = await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion");
        var calls = factory.Indec.Calls;
        await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion?from=2026-09&to=2026-10");

        Assert.True(range.GetProperty("available").GetBoolean());
        Assert.Equal("indec_api", range.GetProperty("source").GetString());
        Assert.Equal(4.1418m, range.GetProperty("percentage").GetDecimal());
        Assert.Equal(2, range.GetProperty("months").GetArrayLength());
        Assert.Equal("2026-10", latest.GetProperty("from").GetString());
        Assert.Equal(2.2m, latest.GetProperty("percentage").GetDecimal());
        Assert.Equal(calls, factory.Indec.Calls);
    }

    [Fact]
    public async Task Suggestion_falls_back_and_then_asks_for_a_manual_value()
    {
        factory.ResetInflation();
        factory.Indec.Fail = true;
        factory.Fallback.Rates[October] = 2.2m;
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var fallback = await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion?from=2026-10&to=2026-10");
        Assert.Equal("argentinadatos", fallback.GetProperty("source").GetString());
        Assert.Equal(2.2m, fallback.GetProperty("percentage").GetDecimal());

        var missingMonth = await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion?from=2026-11&to=2026-11");
        Assert.False(missingMonth.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, missingMonth.GetProperty("percentage").ValueKind);
        Assert.Contains("noviembre de 2026", missingMonth.GetProperty("message").GetString());

        factory.Fallback.Fail = true;
        var down = await admin.GetFromJsonAsync<JsonElement>("/prices/inflation-suggestion?from=2026-09&to=2026-09");
        Assert.False(down.GetProperty("available").GetBoolean());
        Assert.Contains("a mano", down.GetProperty("message").GetString());

        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/prices/inflation-suggestion?from=2026/10"), HttpStatusCode.BadRequest, "bad_request");
        await HttpAssertions.AssertErrorAsync(await admin.GetAsync("/prices/inflation-suggestion?from=2026-12&to=2026-12"), HttpStatusCode.BadRequest, "bad_request");
    }

    [Fact]
    public async Task Preview_matches_what_is_applied_with_rounding_and_history()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var category = await factory.AddCategoryAsync();
        var wax = await factory.AddProductAsync(p => { p.CategoryId = category.Id; p.SalePrice = 1234m; p.Cost = 1000m; p.Name = "Cera"; });
        var gel = await factory.AddProductAsync(p => { p.CategoryId = category.Id; p.SalePrice = 1000m; p.Cost = 500m; p.Name = "Gel"; });
        var free = await factory.AddProductAsync(p => { p.CategoryId = category.Id; p.SalePrice = 0m; p.Name = "Muestra"; });
        var body = new { scopeType = "category", categoryId = category.Id, includeCuts = false, percentage = 10m, rounding = "100" };

        var preview = await (await admin.PostAsJsonAsync("/prices/adjustments/preview", body)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, preview.GetProperty("itemsCount").GetInt32());
        Assert.Equal("Se modificarán 2 precios con un aumento de 10%.", preview.GetProperty("message").GetString());
        var items = preview.GetProperty("items").EnumerateArray().ToList();
        var waxItem = items.Single(i => i.GetProperty("entityId").GetInt32() == wax.Id);
        Assert.Equal(1400m, waxItem.GetProperty("newPrice").GetDecimal());
        Assert.Equal(166m, waxItem.GetProperty("difference").GetDecimal());
        Assert.Equal(28.57m, waxItem.GetProperty("marginPercent").GetDecimal());
        Assert.Equal(1100m, items.Single(i => i.GetProperty("entityId").GetInt32() == gel.Id).GetProperty("newPrice").GetDecimal());
        Assert.DoesNotContain(items, i => i.GetProperty("entityId").GetInt32() == free.Id);

        await HttpAssertions.AssertErrorAsync(await admin.PostAsJsonAsync("/prices/adjustments", body), HttpStatusCode.BadRequest, "confirmation_required");
        var applied = await admin.PostAsJsonAsync("/prices/adjustments", new
        {
            scopeType = "category", categoryId = category.Id, includeCuts = false, percentage = 10m, rounding = "100",
            confirm = true, source = "indec_api", periodFrom = "2026-10", periodTo = "2026-10",
        });

        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var adjustment = await applied.Content.ReadFromJsonAsync<JsonElement>();
        var id = adjustment.GetProperty("id").GetInt32();
        Assert.Equal(2, adjustment.GetProperty("itemsCount").GetInt32());
        Assert.Equal("indec_api", adjustment.GetProperty("source").GetString());
        Assert.Equal($"Categoría {category.Name}", adjustment.GetProperty("scopeDescription").GetString());
        Assert.True(adjustment.GetProperty("canRevert").GetBoolean());
        foreach (var item in items)
        {
            var price = await factory.WithDbAsync(db => db.Products.Where(p => p.Id == item.GetProperty("entityId").GetInt32()).Select(p => p.SalePrice).SingleAsync());
            Assert.Equal(item.GetProperty("newPrice").GetDecimal(), price);
        }

        var history = await factory.WithDbAsync(db => db.PriceHistory.AsNoTracking().Where(h => h.AdjustmentId == id).ToListAsync());
        Assert.Equal(2, history.Count);
        Assert.All(history, h => Assert.Equal(PriceChangeReason.Inflation, h.Reason));
        var listed = await admin.GetFromJsonAsync<JsonElement>($"/prices/history?entityType=product&entityId={wax.Id}");
        Assert.Equal(1234m, listed.GetProperty("items")[0].GetProperty("oldPrice").GetDecimal());
        Assert.Equal("inflation", listed.GetProperty("items")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Adjusting_prices_does_not_touch_booked_appointments_or_existing_orders()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var (branch, barber) = await factory.AddBarberAsync();
        var product = await factory.AddProductAsync(p => p.SalePrice = 2000m);
        var ids = await factory.WithDbAsync(async db =>
        {
            var cut = new Cut { Name = $"Servicio {Guid.NewGuid():N}", Price = 1500m, DurationMinutes = 30 };
            db.Cuts.Add(cut);
            await db.SaveChangesAsync();
            var appointment = new Appointment
            {
                BarberId = barber.Id,
                CutId = cut.Id,
                BranchId = branch.Id,
                ClientName = "Cliente ficticio",
                AppointmentDate = Local(2026, 11, 30, 10),
                EndDate = Local(2026, 11, 30, 10, 30),
                Price = 1500m,
                Status = AppointmentStatus.Pending,
                PaymentStatus = PaymentStatus.Unpaid,
                PaymentMethod = PaymentMethod.Cash,
            };
            var client = new User { Name = "Cliente", Email = $"c-{Guid.NewGuid():N}@example.com", PasswordHash = "x", Role = UserRole.Client };
            db.Users.Add(client);
            db.Appointments.Add(appointment);
            await db.SaveChangesAsync();
            var order = new Order
            {
                ClientId = client.Id,
                BranchId = branch.Id,
                Status = OrderStatus.PendingPayment,
                Total = 2000m,
                PaymentExpiresAt = Now.AddMinutes(20),
                Items = [new OrderItem { ProductId = product.Id, Quantity = 1, UnitPrice = 2000m }],
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return (Cut: cut.Id, Appointment: appointment.Id, Order: order.Id);
        });

        var response = await admin.PostAsJsonAsync("/prices/adjustments", new
        {
            scopeType = "selection", productIds = new[] { product.Id }, includeCuts = true, percentage = 20m, rounding = "none", confirm = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2400m, await factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).Select(p => p.SalePrice).SingleAsync()));
        Assert.Equal(1800m, await factory.WithDbAsync(db => db.Cuts.Where(c => c.Id == ids.Cut).Select(c => c.Price).SingleAsync()));
        Assert.Equal(1500m, await factory.WithDbAsync(db => db.Appointments.Where(a => a.Id == ids.Appointment).Select(a => a.Price).SingleAsync()));
        Assert.Equal(2000m, await factory.WithDbAsync(db => db.OrderItems.Where(i => i.OrderId == ids.Order).Select(i => i.UnitPrice).SingleAsync()));
        Assert.Equal(2000m, await factory.WithDbAsync(db => db.Orders.Where(o => o.Id == ids.Order).Select(o => o.Total).SingleAsync()));
    }

    [Fact]
    public async Task Reverting_restores_prices_and_skips_items_edited_afterwards()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var kept = await factory.AddProductAsync(p => p.SalePrice = 1000m);
        var edited = await factory.AddProductAsync(p => p.SalePrice = 1000m);
        var first = await ApplyAsync(admin, kept.Id, edited.Id);
        var second = await ApplyAsync(admin, kept.Id, edited.Id);

        await HttpAssertions.AssertErrorAsync(await admin.PostAsync($"/prices/adjustments/{first}/revert", null), HttpStatusCode.Conflict, "not_last_adjustment");

        var revertSecond = await admin.PostAsync($"/prices/adjustments/{second}/revert", null);
        Assert.Equal(2, (await revertSecond.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restored").GetInt32());
        await HttpAssertions.AssertErrorAsync(await admin.PostAsync($"/prices/adjustments/{second}/revert", null), HttpStatusCode.Conflict, "already_reverted");

        await admin.PutAsJsonAsync($"/products/{edited.Id}", new { categoryId = edited.CategoryId, name = edited.Name, salePrice = 1500m, minStock = 2 });
        var revertFirst = await admin.PostAsync($"/prices/adjustments/{first}/revert", null);

        Assert.Equal(HttpStatusCode.OK, revertFirst.StatusCode);
        var result = await revertFirst.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("restored").GetInt32());
        var skipped = Assert.Single(result.GetProperty("skipped").EnumerateArray());
        Assert.Equal(edited.Id, skipped.GetProperty("entityId").GetInt32());
        Assert.Equal(1100m, skipped.GetProperty("adjustedPrice").GetDecimal());
        Assert.Equal(1500m, skipped.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(1000m, await factory.WithDbAsync(db => db.Products.Where(p => p.Id == kept.Id).Select(p => p.SalePrice).SingleAsync()));
        Assert.Equal(1500m, await factory.WithDbAsync(db => db.Products.Where(p => p.Id == edited.Id).Select(p => p.SalePrice).SingleAsync()));
        var reverts = await factory.WithDbAsync(db => db.PriceHistory.CountAsync(h => h.Reason == PriceChangeReason.Revert && h.EntityId == kept.Id));
        Assert.Equal(2, reverts);

        var list = await admin.GetFromJsonAsync<List<JsonElement>>("/prices/adjustments");
        Assert.All(list!.Where(a => a.GetProperty("id").GetInt32() == first || a.GetProperty("id").GetInt32() == second),
            a => Assert.NotEqual(JsonValueKind.Null, a.GetProperty("revertedAt").ValueKind));
    }

    [Fact]
    public async Task Adjustment_data_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var invalid = await admin.PostAsJsonAsync("/prices/adjustments/preview", new { scopeType = "otro", percentage = 0m, rounding = "25" });
        var errors = (await HttpAssertions.AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("scopeType", out _));
        Assert.True(errors.TryGetProperty("percentage", out _));
        Assert.True(errors.TryGetProperty("rounding", out _));

        var noProducts = await admin.PostAsJsonAsync("/prices/adjustments/preview", new { scopeType = "selection", productIds = Array.Empty<int>(), percentage = 5m, rounding = "none" });
        await HttpAssertions.AssertErrorAsync(noProducts, HttpStatusCode.BadRequest, "validation_error");
        var missing = await admin.PostAsJsonAsync("/prices/adjustments/preview", new { scopeType = "category", categoryId = 999999, percentage = 5m, rounding = "none" });
        await HttpAssertions.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "category_unavailable");
    }

    private static async Task<int> ApplyAsync(HttpClient admin, params int[] productIds)
    {
        var response = await admin.PostAsJsonAsync("/prices/adjustments", new
        {
            scopeType = "selection", productIds, includeCuts = false, percentage = 10m, rounding = "none", confirm = true,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }
}
