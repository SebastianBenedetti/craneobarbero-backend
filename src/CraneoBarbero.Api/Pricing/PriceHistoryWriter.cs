using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;

namespace CraneoBarbero.Api.Pricing;

public sealed class PriceHistoryWriter(AppDbContext db, BusinessClock clock)
{
    public void Record(
        PriceEntityType entityType,
        int entityId,
        PriceField field,
        decimal oldValue,
        decimal newValue,
        PriceChangeReason reason,
        int userId,
        int? adjustmentId = null)
    {
        if (oldValue == newValue)
        {
            return;
        }

        db.PriceHistory.Add(new PriceHistory
        {
            EntityType = entityType,
            EntityId = entityId,
            Field = field,
            OldPrice = oldValue,
            NewPrice = newValue,
            AdjustmentId = adjustmentId,
            Reason = reason,
            ChangedById = userId,
            ChangedAt = clock.UtcNow,
        });
    }
}
