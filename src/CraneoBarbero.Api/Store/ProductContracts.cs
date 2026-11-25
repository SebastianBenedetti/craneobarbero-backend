using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Catalog;
using CraneoBarbero.Api.Domain.Entities;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record ProductRequest(
    int? CategoryId,
    string? Name,
    string? Description,
    string? Sku,
    string? Barcode,
    decimal? SalePrice,
    decimal? Cost,
    int? MinStock,
    int? ReorderQuantity,
    int? DefaultSupplierId,
    string? ImageUrl,
    bool? IsActive = null);

public sealed record ProductListQuery(int? CategoryId, string? Q, bool? Active, bool? LowStock);

public sealed record ProductCategoryRef(int Id, string Name);

public sealed record ProductSupplierRef(int Id, string Name);

public sealed record AdminProductResponse(
    int Id,
    int CategoryId,
    ProductCategoryRef Category,
    string Name,
    string? Description,
    string? Sku,
    string? Barcode,
    decimal SalePrice,
    decimal Cost,
    decimal Margin,
    decimal? MarginPercent,
    int Stock,
    int MinStock,
    int? ReorderQuantity,
    bool LowStock,
    ProductSupplierRef? DefaultSupplier,
    string? ImageUrl,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AdminProductResponse From(Product p) => new(
        p.Id,
        p.CategoryId,
        new ProductCategoryRef(p.Category!.Id, p.Category.Name),
        p.Name,
        p.Description,
        p.Sku,
        p.Barcode,
        p.SalePrice,
        p.Cost,
        StockMath.Margin(p.SalePrice, p.Cost),
        StockMath.MarginPercent(p.SalePrice, p.Cost),
        p.Stock,
        p.MinStock,
        p.ReorderQuantity,
        StockMath.IsLow(p.Stock, p.MinStock),
        p.DefaultSupplier is null ? null : new ProductSupplierRef(p.DefaultSupplier.Id, p.DefaultSupplier.Name),
        p.ImageUrl,
        p.IsActive,
        p.CreatedAt,
        p.UpdatedAt);
}

public sealed record ClientProductResponse(
    int Id,
    int CategoryId,
    ProductCategoryRef Category,
    string Name,
    string? Description,
    decimal SalePrice,
    string? ImageUrl,
    bool InStock)
{
    public static ClientProductResponse From(Product p) => new(
        p.Id,
        p.CategoryId,
        new ProductCategoryRef(p.Category!.Id, p.Category.Name),
        p.Name,
        p.Description,
        p.SalePrice,
        p.ImageUrl,
        p.Stock > 0);
}

public static class StockMath
{
    public static decimal Margin(decimal salePrice, decimal cost) => salePrice - cost;

    public static decimal? MarginPercent(decimal salePrice, decimal cost) =>
        salePrice > 0 ? decimal.Round((salePrice - cost) / salePrice * 100m, 2) : null;

    public static bool IsLow(int stock, int minStock) => stock <= minStock;
}

public static class StoreLimits
{
    public const decimal MaxAmount = CatalogLimits.MaxPrice;
    public const int MaxQuantity = 1_000_000;
    public const int MaxProductDescriptionLength = 1000;
}

public sealed class ProductRequestValidator : AbstractValidator<ProductRequest>
{
    public ProductRequestValidator()
    {
        RuleFor(r => r.CategoryId).NotNull().WithMessage("Elegí la categoría.")
            .GreaterThan(0).WithMessage("Elegí la categoría.");
        RuleFor(r => r.Name).NotEmpty().WithMessage("Ingresá el nombre del producto.")
            .Must(name => name is not null && name.Trim().Length is >= 2 and <= 150)
            .WithMessage("El nombre debe tener entre 2 y 150 caracteres.");
        RuleFor(r => r.Description).MaximumLength(StoreLimits.MaxProductDescriptionLength)
            .WithMessage($"La descripción admite hasta {StoreLimits.MaxProductDescriptionLength} caracteres.");
        RuleFor(r => r.Sku).Must(sku => string.IsNullOrWhiteSpace(sku) || sku.Trim().Length <= 50)
            .WithMessage("El código interno (SKU) admite hasta 50 caracteres.");
        RuleFor(r => r.Barcode).Must(code => string.IsNullOrWhiteSpace(code) || code.Trim().Length <= 50)
            .WithMessage("El código de barras admite hasta 50 caracteres.");
        RuleFor(r => r.SalePrice).NotNull().WithMessage("Ingresá el precio de venta.")
            .Amount("El precio de venta");
        RuleFor(r => r.Cost).Amount("El costo");
        RuleFor(r => r.MinStock).NotNull().WithMessage("Ingresá el stock mínimo.")
            .InclusiveBetween(0, StoreLimits.MaxQuantity).WithMessage("El stock mínimo no puede ser negativo ni tan alto.");
        RuleFor(r => r.ReorderQuantity).InclusiveBetween(1, StoreLimits.MaxQuantity).When(r => r.ReorderQuantity is not null)
            .WithMessage("La cantidad a reponer debe ser mayor a 0.");
        RuleFor(r => r.DefaultSupplierId).GreaterThan(0).When(r => r.DefaultSupplierId is not null)
            .WithMessage("El proveedor no es válido.");
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
    }
}

internal static class StoreRuleExtensions
{
    public static IRuleBuilderOptions<T, decimal?> Amount<T>(this IRuleBuilder<T, decimal?> rule, string field) =>
        rule.Must(value => value is null || (value.Value >= 0 && value.Value <= StoreLimits.MaxAmount))
            .WithMessage($"{field} no puede ser negativo ni tan alto.")
            .Must(value => value is null || decimal.Round(value.Value, 2) == value.Value)
            .WithMessage($"{field} admite hasta dos decimales.");
}
