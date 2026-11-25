using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Store;

public sealed record StockAdjustmentRequest(int? ProductId, int? Quantity, string? Reason);

public sealed record StockMovementQuery(
    int? ProductId,
    StockMovementType? Type,
    int? SupplierId,
    DateOnly? From,
    DateOnly? To,
    int? Page,
    int? PageSize);

public sealed record StockMovementPersonRef(int Id, string Name);

public sealed record StockMovementResponse(
    int Id,
    int ProductId,
    string ProductName,
    StockMovementType Type,
    int Quantity,
    decimal? UnitCost,
    StockMovementPersonRef? Supplier,
    string? DocumentNumber,
    DateTimeOffset OccurredAt,
    int BalanceAfter,
    int? OrderId,
    StockMovementPersonRef? User,
    string? Reason);

public sealed record StockAlertItem(int Id, string Name, int Stock, int MinStock);

public sealed record StockAlertsResponse(int Count, IReadOnlyList<StockAlertItem> Items);

public static class StockLimits
{
    public const int MaxReasonLength = 300;
}

public sealed class StockAdjustmentRequestValidator : AbstractValidator<StockAdjustmentRequest>
{
    public StockAdjustmentRequestValidator()
    {
        RuleFor(r => r.ProductId).NotNull().WithMessage("Elegí el producto.")
            .GreaterThan(0).WithMessage("Elegí el producto.");
        RuleFor(r => r.Quantity).NotNull().WithMessage("Ingresá la cantidad a sumar o restar.")
            .NotEqual(0).WithMessage("La cantidad no puede ser 0.")
            .InclusiveBetween(-StoreLimits.MaxQuantity, StoreLimits.MaxQuantity).WithMessage("La cantidad es demasiado grande.");
        RuleFor(r => r.Reason).NotEmpty().WithMessage("Escribí el motivo del ajuste.")
            .Must(reason => reason is not null && reason.Trim().Length is >= 3 and <= StockLimits.MaxReasonLength)
            .WithMessage($"El motivo debe tener entre 3 y {StockLimits.MaxReasonLength} caracteres.");
    }
}

public sealed class StockMovementQueryValidator : AbstractValidator<StockMovementQuery>
{
    public StockMovementQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"El tamaño de página debe estar entre 1 y {AppointmentLimits.MaxPageSize}.");
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("La fecha final no puede ser anterior a la inicial.");
    }
}
