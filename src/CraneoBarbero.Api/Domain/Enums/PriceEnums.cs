namespace CraneoBarbero.Api.Domain.Enums;

public enum PriceEntityType
{
    Product,
    Cut,
}

public enum PriceField
{
    Price,
    Cost,
}

public enum PriceChangeReason
{
    Manual,
    Inflation,
    Revert,
    Purchase,
}

public enum PriceAdjustmentSource
{
    IndecApi,
    Manual,
}
