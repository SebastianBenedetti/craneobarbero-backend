using CraneoBarbero.Api.Domain.Enums;
using FluentValidation;

namespace CraneoBarbero.Api.Pricing;

public static class AdjustmentScopes
{
    public const string All = "all";
    public const string Category = "category";
    public const string Selection = "selection";

    public static readonly string[] Values = [All, Category, Selection];
}

public static class AdjustmentRounding
{
    public const string None = "none";
    public static readonly string[] Values = [None, "10", "50", "100"];

    public static decimal Apply(decimal price, decimal percentage, string rounding)
    {
        var raw = price * (1m + percentage / 100m);
        if (rounding == None)
        {
            return Math.Max(0m, decimal.Round(raw, 2, MidpointRounding.AwayFromZero));
        }

        var multiple = decimal.Parse(rounding, System.Globalization.CultureInfo.InvariantCulture);
        var rounded = percentage < 0 ? decimal.Floor(raw / multiple) * multiple : decimal.Ceiling(raw / multiple) * multiple;
        return Math.Max(0m, rounded);
    }
}

public sealed record PriceAdjustmentRequest(
    string? ScopeType,
    int? CategoryId,
    IReadOnlyList<int>? ProductIds,
    bool? IncludeCuts,
    decimal? Percentage,
    string? Rounding,
    bool? Confirm = null,
    PriceAdjustmentSource? Source = null,
    string? PeriodFrom = null,
    string? PeriodTo = null,
    IReadOnlyList<int>? CutIds = null);

public sealed record PriceAdjustmentItem(
    PriceEntityType EntityType,
    int EntityId,
    string Name,
    decimal CurrentPrice,
    decimal NewPrice,
    decimal Difference,
    decimal? Cost,
    decimal? MarginPercent);

public sealed record PriceAdjustmentPreview(
    decimal Percentage,
    string Rounding,
    string ScopeDescription,
    int ItemsCount,
    string Message,
    IReadOnlyList<PriceAdjustmentItem> Items);

public sealed record PriceAdjustmentPerson(int Id, string Name);

public sealed record PriceAdjustmentResponse(
    int Id,
    DateTimeOffset AppliedAt,
    PriceAdjustmentPerson? AppliedBy,
    PriceAdjustmentSource Source,
    decimal Percentage,
    string? PeriodFrom,
    string? PeriodTo,
    string ScopeType,
    string? ScopeDescription,
    string RoundingMode,
    bool IncludesCuts,
    int ItemsCount,
    DateTimeOffset? RevertedAt,
    bool CanRevert);

public sealed record SkippedRevertItem(PriceEntityType EntityType, int EntityId, string Name, decimal AdjustedPrice, decimal CurrentPrice);

public sealed record PriceAdjustmentRevertResponse(int AdjustmentId, int Restored, IReadOnlyList<SkippedRevertItem> Skipped);

public sealed record PriceHistoryQuery(PriceEntityType? EntityType, int? EntityId, int? Page, int? PageSize);

public sealed record PriceHistoryResponse(
    int Id,
    PriceEntityType EntityType,
    int EntityId,
    PriceField Field,
    decimal OldPrice,
    decimal NewPrice,
    PriceChangeReason Reason,
    int? AdjustmentId,
    PriceAdjustmentPerson? ChangedBy,
    DateTimeOffset ChangedAt);

public sealed class PriceAdjustmentRequestValidator : AbstractValidator<PriceAdjustmentRequest>
{
    public PriceAdjustmentRequestValidator()
    {
        RuleFor(r => r.ScopeType).NotEmpty().WithMessage("Elegí qué precios querés actualizar.")
            .Must(scope => scope is null || AdjustmentScopes.Values.Contains(scope))
            .WithMessage("El alcance debe ser todos los productos, una categoría o productos seleccionados.");
        RuleFor(r => r.CategoryId).NotNull().WithMessage("Elegí la categoría.")
            .GreaterThan(0).WithMessage("Elegí la categoría.")
            .When(r => r.ScopeType == AdjustmentScopes.Category);
        RuleFor(r => r.ProductIds)
            .Must((request, ids) => (ids is { Count: > 0 } && ids.All(id => id > 0)) ||
                                    request.CutIds is { Count: > 0 } || (request.CutIds is null && request.IncludeCuts == true))
            .WithMessage("Elegí al menos un producto o un servicio.")
            .Must(ids => ids is null || ids.Count <= 500).WithMessage("Se pueden elegir hasta 500 productos.")
            .When(r => r.ScopeType == AdjustmentScopes.Selection);
        RuleFor(r => r.CutIds)
            .Must(ids => ids is null || (ids.All(id => id > 0) && ids.Count <= 100)).WithMessage("Los servicios elegidos no son válidos.")
            .When(r => r.ScopeType == AdjustmentScopes.Selection);
        RuleFor(r => r.Percentage).NotNull().WithMessage("Ingresá el porcentaje del ajuste.")
            .NotEqual(0m).WithMessage("El porcentaje no puede ser 0.")
            .GreaterThan(-100m).WithMessage("La baja tiene que ser menor al 100%.")
            .LessThanOrEqualTo(100m).WithMessage("El porcentaje no puede ser mayor a 100.")
            .Must(p => p is null || decimal.Round(p.Value, 4) == p.Value).WithMessage("El porcentaje admite hasta cuatro decimales.");
        RuleFor(r => r.Rounding).NotEmpty().WithMessage("Elegí el redondeo.")
            .Must(rounding => rounding is null || AdjustmentRounding.Values.Contains(rounding))
            .WithMessage("El redondeo debe ser sin redondeo o al múltiplo de $10, $50 o $100.");
        RuleFor(r => r.PeriodFrom).Matches(@"^\d{4}-\d{2}$").When(r => r.PeriodFrom is not null)
            .WithMessage("El mes debe tener el formato AAAA-MM.");
        RuleFor(r => r.PeriodTo).Matches(@"^\d{4}-\d{2}$").When(r => r.PeriodTo is not null)
            .WithMessage("El mes debe tener el formato AAAA-MM.");
    }
}

public sealed class PriceHistoryQueryValidator : AbstractValidator<PriceHistoryQuery>
{
    public PriceHistoryQueryValidator()
    {
        RuleFor(q => q.EntityId).GreaterThan(0).When(q => q.EntityId is not null).WithMessage("El identificador no es válido.");
        RuleFor(q => q.EntityType).NotNull().When(q => q.EntityId is not null).WithMessage("Indicá si es un producto o un servicio.");
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100).When(q => q.PageSize is not null)
            .WithMessage("El tamaño de página debe estar entre 1 y 100.");
    }
}
