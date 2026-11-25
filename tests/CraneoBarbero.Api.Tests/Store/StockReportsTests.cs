using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class StockReportsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("/stock/valuation")]
    [InlineData("/stock/reorder")]
    public async Task Reports_are_admin_only(string url)
    {
        await HttpAssertions.AssertErrorAsync(await factory.CreateClient().GetAsync(url), HttpStatusCode.Unauthorized, "unauthorized");
        var client = await factory.ClientForAsync(UserRole.Client);
        await HttpAssertions.AssertErrorAsync(await client.GetAsync(url), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Valuation_and_reorder_use_stock_cost_and_margin()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var supplier = await factory.AddSupplierAsync();
        var wax = await factory.AddProductAsync(p =>
        {
            p.Name = "Cera";
            p.Stock = 3;
            p.MinStock = 5;
            p.Cost = 800m;
            p.SalePrice = 1000m;
            p.ReorderQuantity = 10;
            p.DefaultSupplierId = supplier.Id;
        });
        var shampoo = await factory.AddProductAsync(p =>
        {
            p.Name = "Shampoo";
            p.Stock = 10;
            p.MinStock = 2;
            p.Cost = 100m;
            p.SalePrice = 250m;
        });
        var comb = await factory.AddProductAsync(p =>
        {
            p.Name = "Peine";
            p.Stock = 0;
            p.MinStock = 0;
            p.Cost = 50m;
            p.SalePrice = 0m;
        });
        await factory.AddProductAsync(p => { p.Name = "Discontinuado"; p.Stock = 0; p.IsActive = false; });

        var valuation = await admin.GetFromJsonAsync<JsonElement>("/stock/valuation");

        Assert.Equal(13, valuation.GetProperty("totalUnits").GetInt32());
        Assert.Equal(3400m, valuation.GetProperty("totalCostValue").GetDecimal());
        Assert.Equal(5500m, valuation.GetProperty("totalSaleValue").GetDecimal());
        var items = valuation.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([wax.Id, shampoo.Id, comb.Id], items.Select(i => i.GetProperty("id").GetInt32()));
        Assert.Equal(2400m, items[0].GetProperty("costValue").GetDecimal());
        Assert.Equal(200m, items[0].GetProperty("margin").GetDecimal());
        Assert.Equal(20m, items[0].GetProperty("marginPercent").GetDecimal());
        Assert.Equal(60m, items[1].GetProperty("marginPercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("marginPercent").ValueKind);

        var reorder = await admin.GetFromJsonAsync<JsonElement>("/stock/reorder");

        Assert.Equal(2, reorder.GetProperty("count").GetInt32());
        Assert.Equal(8000m, reorder.GetProperty("estimatedTotal").GetDecimal());
        var toBuy = reorder.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(wax.Id, toBuy[0].GetProperty("id").GetInt32());
        Assert.Equal(10, toBuy[0].GetProperty("suggestedQuantity").GetInt32());
        Assert.Equal(8000m, toBuy[0].GetProperty("estimatedCost").GetDecimal());
        Assert.Equal(supplier.Id, toBuy[0].GetProperty("defaultSupplier").GetProperty("id").GetInt32());
        Assert.Equal(comb.Id, toBuy[1].GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, toBuy[1].GetProperty("suggestedQuantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, toBuy[1].GetProperty("defaultSupplier").ValueKind);
    }
}
