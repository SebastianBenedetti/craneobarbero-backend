using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Tests.Store;

public sealed class ProductsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Visitors_and_barbers_see_only_active_products_without_cost_or_stock()
    {
        var active = await factory.AddProductAsync();
        var inactive = await factory.AddProductAsync(p => p.IsActive = false);

        foreach (var reader in new[] { factory.CreateClient(), await factory.ClientForAsync(UserRole.Barber) })
        {
            var response = await reader.GetAsync("/products");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
            var shown = Assert.Single(items, p => p.GetProperty("id").GetInt32() == active.Id);
            Assert.False(shown.TryGetProperty("cost", out _));
            Assert.False(shown.TryGetProperty("stock", out _));
            Assert.DoesNotContain(items, p => p.GetProperty("id").GetInt32() == inactive.Id);

            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/products/{active.Id}")).StatusCode);
            await HttpAssertions.AssertErrorAsync(await reader.GetAsync($"/products/{inactive.Id}"), HttpStatusCode.NotFound, "not_found");
        }
    }

    [Fact]
    public async Task Only_the_admin_creates_or_edits_products()
    {
        foreach (var role in new[] { UserRole.Client, UserRole.Barber })
        {
            var client = await factory.ClientForAsync(role);
            await HttpAssertions.AssertErrorAsync(await client.PostAsJsonAsync("/products", new { }), HttpStatusCode.Forbidden, "forbidden");
            await HttpAssertions.AssertErrorAsync(await client.PutAsJsonAsync("/products/1", new { }), HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Admin_creates_a_product_that_starts_without_stock()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var category = await factory.AddCategoryAsync();
        var supplier = await factory.AddSupplierAsync();
        var sku = $"SKU-{Guid.NewGuid():N}"[..20];

        var created = await admin.PostAsJsonAsync("/products", new
        {
            categoryId = category.Id,
            name = " Cera mate ",
            sku,
            salePrice = 12000m,
            cost = 8000m,
            minStock = 3,
            reorderQuantity = 12,
            defaultSupplierId = supplier.Id,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var product = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cera mate", product.GetProperty("name").GetString());
        Assert.Equal(0, product.GetProperty("stock").GetInt32());
        Assert.True(product.GetProperty("lowStock").GetBoolean());
        Assert.Equal(4000m, product.GetProperty("margin").GetDecimal());
        Assert.Equal(33.33m, product.GetProperty("marginPercent").GetDecimal());
        Assert.Equal(category.Name, product.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal(supplier.Id, product.GetProperty("defaultSupplier").GetProperty("id").GetInt32());

        var duplicate = await admin.PostAsJsonAsync("/products", new { categoryId = category.Id, name = "Otra cera", sku, salePrice = 1m, minStock = 0 });
        await HttpAssertions.AssertErrorAsync(duplicate, HttpStatusCode.Conflict, "sku_in_use");
    }

    [Fact]
    public async Task Editing_price_or_cost_by_hand_is_kept_in_the_history()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var product = await factory.AddProductAsync();

        var updated = await admin.PutAsJsonAsync($"/products/{product.Id}", new
        {
            categoryId = product.CategoryId,
            name = product.Name,
            salePrice = 1500m,
            cost = 700m,
            minStock = 2,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, body.GetProperty("stock").GetInt32());
        var history = await factory.WithDbAsync(db => db.PriceHistory.AsNoTracking()
            .Where(h => h.EntityType == PriceEntityType.Product && h.EntityId == product.Id)
            .OrderBy(h => h.Field)
            .ToListAsync());
        Assert.Collection(history,
            price =>
            {
                Assert.Equal(PriceField.Price, price.Field);
                Assert.Equal(1000m, price.OldPrice);
                Assert.Equal(1500m, price.NewPrice);
                Assert.Equal(PriceChangeReason.Manual, price.Reason);
            },
            cost =>
            {
                Assert.Equal(PriceField.Cost, cost.Field);
                Assert.Equal(600m, cost.OldPrice);
                Assert.Equal(700m, cost.NewPrice);
            });

        var withoutCost = await admin.PutAsJsonAsync($"/products/{product.Id}", new
        {
            categoryId = product.CategoryId,
            name = product.Name,
            salePrice = 1500m,
            minStock = 2,
        });
        Assert.Equal(700m, (await withoutCost.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cost").GetDecimal());
        Assert.Equal(2, await factory.WithDbAsync(db => db.PriceHistory.CountAsync(h => h.EntityType == PriceEntityType.Product && h.EntityId == product.Id)));
    }

    [Fact]
    public async Task Clients_only_see_active_products_without_cost_or_stock_details()
    {
        var client = await factory.ClientForAsync(UserRole.Client);
        var active = await factory.AddProductAsync(p => p.Stock = 0);
        var inactive = await factory.AddProductAsync(p => p.IsActive = false);

        var list = await client.GetFromJsonAsync<List<JsonElement>>("/products");

        var shown = Assert.Single(list!, p => p.GetProperty("id").GetInt32() == active.Id);
        Assert.False(shown.GetProperty("inStock").GetBoolean());
        Assert.False(shown.TryGetProperty("cost", out _));
        Assert.False(shown.TryGetProperty("stock", out _));
        Assert.DoesNotContain(list!, p => p.GetProperty("id").GetInt32() == inactive.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/products/{inactive.Id}")).StatusCode);

        var admin = await factory.ClientForAsync(UserRole.Admin);
        var adminView = await admin.GetFromJsonAsync<JsonElement>($"/products/{inactive.Id}");
        Assert.False(adminView.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Admin_filters_by_low_stock_and_search_text()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var low = await factory.AddProductAsync(p => { p.Stock = 1; p.MinStock = 3; p.Name = $"Shampoo {Guid.NewGuid():N}"; });
        var enough = await factory.AddProductAsync(p => { p.Stock = 20; p.MinStock = 3; });

        var lowList = await admin.GetFromJsonAsync<List<JsonElement>>("/products?lowStock=true");
        Assert.Contains(lowList!, p => p.GetProperty("id").GetInt32() == low.Id);
        Assert.DoesNotContain(lowList!, p => p.GetProperty("id").GetInt32() == enough.Id);

        var found = await admin.GetFromJsonAsync<List<JsonElement>>($"/products?q={Uri.EscapeDataString(low.Name[..12].ToUpperInvariant())}");
        Assert.Contains(found!, p => p.GetProperty("id").GetInt32() == low.Id);
    }

    [Fact]
    public async Task Product_references_and_data_are_validated()
    {
        var admin = await factory.ClientForAsync(UserRole.Admin);
        var category = await factory.AddCategoryAsync();
        var inactiveSupplier = await factory.AddSupplierAsync(isActive: false);

        var invalid = await admin.PostAsJsonAsync("/products", new { name = "x", salePrice = -1m, cost = 1.234m, minStock = -1, reorderQuantity = 0 });
        var errors = (await HttpAssertions.AssertErrorAsync(invalid, HttpStatusCode.BadRequest, "validation_error")).GetProperty("errors");
        foreach (var field in new[] { "categoryId", "name", "salePrice", "cost", "minStock", "reorderQuantity" })
        {
            Assert.True(errors.TryGetProperty(field, out _), field);
        }

        var missingCategory = await admin.PostAsJsonAsync("/products", new { categoryId = 999999, name = "Peine", salePrice = 10m, minStock = 0 });
        await HttpAssertions.AssertErrorAsync(missingCategory, HttpStatusCode.BadRequest, "category_unavailable");

        var badSupplier = await admin.PostAsJsonAsync("/products", new { categoryId = category.Id, name = "Peine", salePrice = 10m, minStock = 0, defaultSupplierId = inactiveSupplier.Id });
        await HttpAssertions.AssertErrorAsync(badSupplier, HttpStatusCode.BadRequest, "supplier_unavailable");
    }
}
