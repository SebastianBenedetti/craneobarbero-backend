using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Store;

public sealed class OrderManagement(
    AppDbContext db,
    StockLedger ledger,
    OrderTransitions transitions,
    OrderPayments payments,
    BusinessClock clock,
    IOptions<BusinessSettings> business)
{
    public async Task<Order> CreateAsync(int clientId, CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var branchId = request.BranchId!.Value;
        if (!await db.Branches.AnyAsync(b => b.Id == branchId && b.IsActive, cancellationToken))
        {
            throw new BadRequestException("La sucursal elegida no existe o está desactivada.", ErrorCodes.BranchInactive);
        }

        var lines = request.Items!;
        var ids = lines.Select(i => i.ProductId!.Value).ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var order = new Order
        {
            ClientId = clientId,
            BranchId = branchId,
            Status = OrderStatus.PendingPayment,
            RefundStatus = RefundStatus.None,
            PaymentExpiresAt = clock.UtcNow.AddMinutes(business.Value.PaymentExpirationMinutes),
        };

        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId!.Value, out var product) || !product.IsActive)
            {
                throw new BadRequestException(
                    product is null ? "Uno de los productos ya no existe." : $"{product.Name} ya no está a la venta.",
                    ErrorCodes.ProductUnavailable);
            }

            var quantity = line.Quantity!.Value;
            if (product.Stock < quantity)
            {
                throw new ConflictException(
                    product.Stock == 0
                        ? $"{product.Name} no tiene stock."
                        : $"No hay stock suficiente de {product.Name}: quedan {product.Stock}.",
                    ErrorCodes.InsufficientStock);
            }

            order.Items.Add(new OrderItem { ProductId = product.Id, Quantity = quantity, UnitPrice = product.SalePrice });
        }

        order.Total = order.Items.Sum(i => i.Quantity * i.UnitPrice);
        if (order.Total <= 0)
        {
            throw new BadRequestException("El pedido no tiene importe para pagar.", ErrorCodes.NotPayable);
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        return order;
    }

    public async Task<Order> UpdateStatusAsync(int orderId, OrderStatus status, CancellationToken cancellationToken)
    {
        var order = await FindAsync(orderId, cancellationToken);
        var from = status == OrderStatus.Ready ? OrderStatus.Paid : OrderStatus.Ready;
        if (order.Status == status)
        {
            return order;
        }

        if (!await transitions.TryMoveAsync(order, from, status, cancellationToken))
        {
            throw new ConflictException(
                status == OrderStatus.Ready
                    ? "Solo un pedido pagado puede pasar a listo para retirar."
                    : "Solo un pedido listo para retirar puede pasar a entregado.",
                ErrorCodes.InvalidStatusTransition);
        }

        await db.SaveChangesAsync(cancellationToken);
        return order;
    }

    public async Task<Order> CancelAsync(int orderId, int adminId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var order = await FindAsync(orderId, cancellationToken);
        var reason = AccountRules.TrimToNull(request.Reason);
        var current = order.Status;

        if (current == OrderStatus.PendingPayment)
        {
            if (!await transitions.TryMoveAsync(order, OrderStatus.PendingPayment, OrderStatus.Cancelled, cancellationToken))
            {
                throw NotCancellable();
            }

            await payments.ReleaseCheckoutAsync(order, cancellationToken);
            Close(order, adminId, reason, RefundStatus.None);
            await db.SaveChangesAsync(cancellationToken);
            return order;
        }

        if (current is not (OrderStatus.Paid or OrderStatus.Ready))
        {
            throw NotCancellable();
        }

        var refund = request.Refund!.Value;
        await using var transaction = await ledger.BeginAsync(cancellationToken);
        if (!await transitions.TryMoveAsync(order, current, OrderStatus.Cancelled, cancellationToken))
        {
            throw NotCancellable();
        }

        var now = clock.UtcNow;
        foreach (var item in order.Items.OrderBy(i => i.ProductId))
        {
            await ledger.ApplyAsync(new StockChange(
                item.ProductId,
                StockMovementType.Refund,
                item.Quantity,
                adminId,
                now,
                UnitCost: item.UnitCost,
                OrderId: order.Id,
                Reason: "Pedido cancelado"), cancellationToken);
        }

        if (refund && !await payments.RefundAsync(order, cancellationToken))
        {
            throw new ConflictException("Este pedido no tiene un pago para reembolsar.", ErrorCodes.NotRefundable);
        }

        Close(order, adminId, reason, refund ? RefundStatus.Refunded : RefundStatus.NotRefunded);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return order;
    }

    private static void Close(Order order, int adminId, string? reason, RefundStatus refundStatus)
    {
        order.Status = OrderStatus.Cancelled;
        order.PaymentExpiresAt = null;
        order.CancelledById = adminId;
        order.CancellationReason = reason;
        order.RefundStatus = refundStatus;
    }

    private static ConflictException NotCancellable() =>
        new("Este pedido ya no se puede cancelar.", ErrorCodes.NotCancellable);

    private async Task<Order> FindAsync(int orderId, CancellationToken cancellationToken) =>
        await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
        ?? throw new NotFoundException("No se encontró el pedido.");
}
