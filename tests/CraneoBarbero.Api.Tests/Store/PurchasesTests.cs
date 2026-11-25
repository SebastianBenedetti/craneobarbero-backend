using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using CraneoBarbero.Api.Tests.Schedule;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class PurchasesTests(ScheduleApiFactory factory) : IClassFixture<ScheduleApiFactory>
{
    private static readonly DateOnly Today = new(2026, 11, 23);

    [Fact]
    public async Task Only_the_admin_registers_purchases()
    {
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().PostAsJsonAsync("/stock/purchases", new { }), HttpStatusCode.Unauthorized, "unauthorized");
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/stock/purchases", new { }), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Buying_at_a_new_cost_updates_the_cost_and_keeps_the_history()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var supplier = await factory.AddSupplierAsync();
        var product = await factory.AddProductAsync(p => { p.Cost = 80m; p.Stock = 4; });

        var response = await admin.PostAsJsonAsync("/stock/purchases", new
        {
            supplierId = supplier.Id,
            documentNumber = " R-0001-00001234 ",
            date = Today,
            items = new[] { new { productId = product.Id, quantity = 6, unitCost = 100m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(600m, purchase.GetProperty("total").GetDecimal());
        Assert.Equal(supplier.Id, purchase.GetProperty("supplier").GetProperty("id").GetInt32());
        Assert.Equal(ScheduleApiFactory.Now, purchase.GetProperty("occurredAt").GetDateTimeOffset());
        var line = Assert.Single(purchase.GetProperty("items").EnumerateArray());
        Assert.Equal(80m, line.GetProperty("previousCost").GetDecimal());
        Assert.Equal(100m, line.GetProperty("newCost").GetDecimal());
        Assert.Equal(10, line.GetProperty("stockAfter").GetInt32());

        var stored = await factory.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id));
        Assert.Equal(100m, stored.Cost);
        Assert.Equal(10, stored.Stock);
        var history = Assert.Single(await factory.WithDbAsync(db => db.PriceHistory.AsNoTracking()
            .Where(h => h.EntityType == PriceEntityType.Product && h.EntityId == product.Id).ToListAsync()));
        Assert.Equal(PriceField.Cost, history.Field);
        Assert.Equal(PriceChangeReason.Purchase, history.Reason);
        Assert.Equal(80m, history.OldPrice);
        Assert.Equal(100m, history.NewPrice);
        var movement = Assert.Single(await factory.WithDbAsync(db => db.StockMovements.AsNoTracking()
            .Where(m => m.ProductId == product.Id).ToListAsync()));
        Assert.Equal(StockMovementType.In, movement.Type);
        Assert.Equal(100m, movement.UnitCost);
        Assert.Equal(supplier.Id, movement.SupplierId);
        Assert.Equal("R-0001-00001234", movement.DocumentNumber);
        Assert.Equal(10, movement.BalanceAfter);
    }

    [Fact]
    public async Task A_purchase_with_several_lines_only_records_cost_changes()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var sameCost = await factory.AddProductAsync(p => { p.Cost = 50m; p.Stock = 0; });
        var newCost = await factory.AddProductAsync(p => { p.Cost = 70m; p.Stock = 1; });

        var response = await admin.PostAsJsonAsync("/stock/purchases", new
        {
            date = Today.AddDays(-2),
            items = new[]
            {
                new { productId = newCost.Id, quantity = 2, unitCost = 75.5m },
                new { productId = sameCost.Id, quantity = 3, unitCost = 50m },
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchase = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, purchase.GetProperty("supplier").ValueKind);
        Assert.Equal(301m, purchase.GetProperty("total").GetDecimal());
        Assert.Equal(ScheduleApiFactory.Local(2026, 11, 21, 12), purchase.GetProperty("occurredAt").GetDateTimeOffset());
        var lines = purchase.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(newCost.Id, lines[0].GetProperty("productId").GetInt32());
        Assert.Equal(3, lines[0].GetProperty("stockAfter").GetInt32());

        var historyCount = await factory.WithDbAsync(db => db.PriceHistory.CountAsync(h =>
            h.EntityType == PriceEntityType.Product && (h.EntityId == sameCost.Id || h.EntityId == newCost.Id)));
        Assert.Equal(1, historyCount);
    }

    [Fact]
    public async Task Purchase_data_is_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var product = await factory.AddProductAsync();

        var empty = await admin.PostAsJsonAsync("/stock/purchases", new { date = Today.AddDays(1), items = Array.Empty<object>() });
        var errors = (await HttpAssertions.AssertErrorAsync(empty, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("date", out _));
        Assert.True(errors.TryGetProperty("items", out _));

        var badLines = await admin.PostAsJsonAsync("/stock/purchases", new
        {
            date = Today,
            items = new[]
            {
                new { productId = product.Id, quantity = 0, unitCost = -1m },
                new { productId = product.Id, quantity = 1, unitCost = 1m },
            },
        });
        await HttpAssertions.AssertErrorAsync(badLines, HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Unknown_supplier_or_product_is_rejected_without_moving_stock()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var inactiveSupplier = await factory.AddSupplierAsync(isActive: false);
        var product = await factory.AddProductAsync();

        var supplierResponse = await admin.PostAsJsonAsync("/stock/purchases", new
        {
            supplierId = inactiveSupplier.Id,
            date = Today,
            items = new[] { new { productId = product.Id, quantity = 1, unitCost = 1m } },
        });
        await HttpAssertions.AssertErrorAsync(supplierResponse, HttpStatusCode.BadRequest, "supplier_unavailable");

        var productResponse = await admin.PostAsJsonAsync("/stock/purchases", new
        {
            date = Today,
            items = new[] { new { productId = 999999, quantity = 1, unitCost = 1m } },
        });
        await HttpAssertions.AssertErrorAsync(productResponse, HttpStatusCode.BadRequest, "product_unavailable");
        Assert.Equal(0, await factory.WithDbAsync(db => db.StockMovements.CountAsync(m => m.ProductId == product.Id)));
    }
}
