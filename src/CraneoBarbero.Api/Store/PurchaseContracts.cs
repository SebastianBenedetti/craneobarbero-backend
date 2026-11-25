using CraneoBarbero.Api.Configuration;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record PurchaseItemRequest(int? ProductId, int? Quantity, decimal? UnitCost);

public sealed record PurchaseRequest(
    int? SupplierId,
    string? DocumentNumber,
    DateOnly? Date,
    IReadOnlyList<PurchaseItemRequest>? Items);

public sealed record PurchaseItemResponse(
    int MovementId,
    int ProductId,
    string ProductName,
    int Quantity,
    decimal UnitCost,
    decimal PreviousCost,
    decimal NewCost,
    int StockAfter);

public sealed record PurchaseResponse(
    StockMovementPersonRef? Supplier,
    string? DocumentNumber,
    DateTimeOffset OccurredAt,
    decimal Total,
    IReadOnlyList<PurchaseItemResponse> Items);

public static class PurchaseLimits
{
    public const int MaxItems = 50;
    public const int MaxDocumentNumberLength = 50;
}

public sealed class PurchaseRequestValidator : AbstractValidator<PurchaseRequest>
{
    public PurchaseRequestValidator(BusinessClock clock)
    {
        RuleFor(r => r.SupplierId).GreaterThan(0).When(r => r.SupplierId is not null)
            .WithMessage("El proveedor no es válido.");
        RuleFor(r => r.DocumentNumber)
            .Must(number => string.IsNullOrWhiteSpace(number) || number.Trim().Length <= PurchaseLimits.MaxDocumentNumberLength)
            .WithMessage($"El número de comprobante admite hasta {PurchaseLimits.MaxDocumentNumberLength} caracteres.");
        RuleFor(r => r.Date).NotNull().WithMessage("Ingresá la fecha de la compra.")
            .Must(date => date is null || date.Value <= clock.Today)
            .WithMessage("La fecha de la compra no puede ser futura.");
        RuleFor(r => r.Items).NotEmpty().WithMessage("Agregá al menos un producto.")
            .Must(items => items is null || items.Count <= PurchaseLimits.MaxItems)
            .WithMessage($"Una compra admite hasta {PurchaseLimits.MaxItems} líneas.")
            .Must(items => items is null || items.Where(i => i?.ProductId is not null).GroupBy(i => i.ProductId).All(g => g.Count() == 1))
            .WithMessage("Cada producto puede aparecer una sola vez en la compra.");
        RuleForEach(r => r.Items).NotNull().WithMessage("La línea no es válida.").ChildRules(item =>
        {
            item.RuleFor(i => i.ProductId).NotNull().WithMessage("Elegí el producto.")
                .GreaterThan(0).WithMessage("Elegí el producto.");
            item.RuleFor(i => i.Quantity).NotNull().WithMessage("Ingresá la cantidad.")
                .InclusiveBetween(1, StoreLimits.MaxQuantity).WithMessage("La cantidad debe ser mayor a 0.");
            item.RuleFor(i => i.UnitCost).NotNull().WithMessage("Ingresá el costo unitario.")
                .Amount("El costo unitario");
        });
    }
}
