using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class StockMovementsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "POST", "/stock/movements" },
        { "GET", "/stock/movements" },
        { "GET", "/stock/alerts" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Stock_endpoints_are_admin_only(string method, string url)
    {
        var anonymous = await factory.CreateClient().SendAsync(Request(method, url));
        await HttpAssertions.AssertErrorAsync(anonymous, HttpStatusCode.Unauthorized, "unauthorized");

        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.SendAsync(Request(method, url)), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Adjustments_change_the_stock_and_record_the_balance()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var product = await factory.AddProductAsync(p => p.Stock = 10);

        var down = await admin.PostAsJsonAsync("/stock/movements", new { productId = product.Id, quantity = -3, reason = " Rotura " });
        var up = await admin.PostAsJsonAsync("/stock/movements", new { productId = product.Id, quantity = 5, reason = "Conteo físico" });

        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var first = await down.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("adjustment", first.GetProperty("type").GetString());
        Assert.Equal(-3, first.GetProperty("quantity").GetInt32());
        Assert.Equal(7, first.GetProperty("balanceAfter").GetInt32());
        Assert.Equal("Rotura", first.GetProperty("reason").GetString());
        Assert.Equal(product.Name, first.GetProperty("productName").GetString());
        Assert.Equal(JsonValueKind.Object, first.GetProperty("user").ValueKind);
        Assert.Equal(12, (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("balanceAfter").GetInt32());
        var stored = await factory.WithDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.Id == product.Id));
        Assert.Equal(12, stored.Stock);
        Assert.Equal(600m, stored.Cost);
    }

    [Fact]
    public async Task Stock_never_goes_negative()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var product = await factory.AddProductAsync(p => p.Stock = 2);

        var response = await admin.PostAsJsonAsync("/stock/movements", new { productId = product.Id, quantity = -3, reason = "Rotura" });

        await HttpAssertions.AssertErrorAsync(response, HttpStatusCode.Conflict, "insufficient_stock");
        Assert.Equal(2, await factory.WithDbAsync(db => db.Products.Where(p => p.Id == product.Id).Select(p => p.Stock).SingleAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.StockMovements.CountAsync(m => m.ProductId == product.Id)));
    }

    [Fact]
    public async Task Adjustments_need_a_quantity_and_an_existing_product_but_the_reason_is_optional()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);

        var invalid = await admin.PostAsJsonAsync("/stock/movements", new { productId = 1, quantity = 0, reason = new string('x', 301) });
        var errors = (await HttpAssertions.AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("quantity", out _));
        Assert.True(errors.TryGetProperty("reason", out _));

        var product = await factory.AddProductAsync(p => p.Stock = 2);
        var withoutReason = await admin.PostAsJsonAsync("/stock/movements", new { productId = product.Id, quantity = 1, reason = "" });
        Assert.Equal(HttpStatusCode.Created, withoutReason.StatusCode);
        Assert.Equal("Ajuste manual", await factory.WithDbAsync(db =>
            db.StockMovements.Where(m => m.ProductId == product.Id).Select(m => m.Reason).SingleAsync()));

        var missing = await admin.PostAsJsonAsync("/stock/movements", new { productId = 999999, quantity = 1, reason = "Conteo" });
        await HttpAssertions.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "product_unavailable");
    }

    [Fact]
    public async Task Movements_are_listed_newest_first_with_filters_and_pages()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var product = await factory.AddProductAsync(p => p.Stock = 10);
        foreach (var quantity in new[] { 1, 2, 3 })
        {
            await admin.PostAsJsonAsync("/stock/movements", new { productId = product.Id, quantity, reason = "Conteo" });
        }

        var page = await admin.GetFromJsonAsync<JsonElement>($"/stock/movements?productId={product.Id}&type=adjustment&pageSize=2");

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(3, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(16, items[0].GetProperty("balanceAfter").GetInt32());

        var none = await admin.GetFromJsonAsync<JsonElement>($"/stock/movements?productId={product.Id}&type=in");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Alerts_list_active_products_at_or_below_the_minimum()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var atMinimum = await factory.AddProductAsync(p => { p.Stock = 2; p.MinStock = 2; });
        var above = await factory.AddProductAsync(p => { p.Stock = 3; p.MinStock = 2; });
        var inactive = await factory.AddProductAsync(p => { p.Stock = 0; p.MinStock = 2; p.IsActive = false; });

        var alerts = await admin.GetFromJsonAsync<JsonElement>("/stock/alerts");

        var ids = alerts.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(atMinimum.Id, ids);
        Assert.DoesNotContain(above.Id, ids);
        Assert.DoesNotContain(inactive.Id, ids);
        Assert.Equal(ids.Count, alerts.GetProperty("count").GetInt32());
    }

    private static HttpRequestMessage Request(string method, string url) => new(new HttpMethod(method), url)
    {
        Content = method == "GET" ? null : JsonContent.Create(new { }),
    };
}
