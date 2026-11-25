using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Store;

public sealed class OrderTransitions(AppDbContext db, BusinessClock clock)
{
    public async Task<bool> TryMoveAsync(Order order, OrderStatus from, OrderStatus to, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var now = clock.UtcNow;
            var updated = await db.Orders
                .Where(o => o.Id == order.Id && o.Status == from)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(o => o.Status, to)
                    .SetProperty(o => o.UpdatedAt, now), cancellationToken);
            if (updated == 0)
            {
                return false;
            }
        }
        else if (order.Status != from)
        {
            return false;
        }

        order.Status = to;
        return true;
    }

    public async Task<int> ExpireUnpaidAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = db.Orders.Where(o =>
            o.Status == OrderStatus.PendingPayment && o.PaymentExpiresAt != null && o.PaymentExpiresAt < now);

        if (db.Database.IsRelational())
        {
            return await due.ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, OrderStatus.Expired)
                .SetProperty(o => o.UpdatedAt, now), cancellationToken);
        }

        var rows = await due.ToListAsync(cancellationToken);
        rows.ForEach(o => o.Status = OrderStatus.Expired);
        await db.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }
}
