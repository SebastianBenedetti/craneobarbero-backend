namespace CraneoBarbero.Api.Store;

public sealed record ValuationItem(
    int Id,
    string Name,
    ProductCategoryRef Category,
    bool IsActive,
    int Stock,
    decimal Cost,
    decimal CostValue,
    decimal SalePrice,
    decimal SaleValue,
    decimal Margin,
    decimal? MarginPercent);

public sealed record ValuationResponse(
    int TotalUnits,
    decimal TotalCostValue,
    decimal TotalSaleValue,
    IReadOnlyList<ValuationItem> Items);

public sealed record ReorderItem(
    int Id,
    string Name,
    string? Sku,
    int Stock,
    int MinStock,
    int? SuggestedQuantity,
    decimal Cost,
    decimal? EstimatedCost,
    ProductSupplierRef? DefaultSupplier);

public sealed record ReorderResponse(int Count, decimal EstimatedTotal, IReadOnlyList<ReorderItem> Items);
