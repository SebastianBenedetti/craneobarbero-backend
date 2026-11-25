using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Tests.Infrastructure;

namespace CraneoBarbero.Api.Tests.Store;

public static class StoreSeed
{
    public static Task<ProductCategory> AddCategoryAsync(this ApiFactory factory) =>
        factory.WithDbAsync(async db =>
        {
            var category = new ProductCategory { Name = $"Categoría {Guid.NewGuid():N}" };
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            return category;
        });

    public static Task<Supplier> AddSupplierAsync(this ApiFactory factory, bool isActive = true) =>
        factory.WithDbAsync(async db =>
        {
            var supplier = new Supplier { Name = $"Proveedor {Guid.NewGuid():N}", IsActive = isActive };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
            return supplier;
        });

    public static async Task<Product> AddProductAsync(this ApiFactory factory, Action<Product>? change = null)
    {
        var category = await factory.AddCategoryAsync();
        return await factory.WithDbAsync(async db =>
        {
            var product = new Product
            {
                CategoryId = category.Id,
                Name = $"Producto {Guid.NewGuid():N}",
                SalePrice = 1000m,
                Cost = 600m,
                Stock = 10,
                MinStock = 2,
            };
            change?.Invoke(product);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return product;
        });
    }
}
