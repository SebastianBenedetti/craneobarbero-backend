using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Domain.Entities;

public class PriceAdjustment
{
    public int Id { get; set; }
    public int AppliedById { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public PriceAdjustmentSource Source { get; set; }
    public decimal Percentage { get; set; }
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public string? ScopeDescription { get; set; }
    public string RoundingMode { get; set; } = string.Empty;
    public bool IncludesCuts { get; set; }
    public int ItemsCount { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
    public int? RevertedById { get; set; }
}

public class PriceHistory
{
    public int Id { get; set; }
    public PriceEntityType EntityType { get; set; }
    public int EntityId { get; set; }
    public PriceField Field { get; set; }
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public int? AdjustmentId { get; set; }
    public PriceChangeReason Reason { get; set; }
    public int ChangedById { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}
