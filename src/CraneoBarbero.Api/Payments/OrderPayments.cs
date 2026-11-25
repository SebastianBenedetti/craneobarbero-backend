using System.Globalization;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Store;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CraneoBarbero.Api.Payments;

public sealed class OrderPayments(
    AppDbContext db,
    IPaymentGateway gateway,
    StockLedger ledger,
    OrderTransitions transitions,
    BusinessClock clock,
    ILogger<OrderPayments> logger)
{
    public const string ReferencePrefix = "order:";

    public static string ReferenceFor(int orderId) => ReferencePrefix + orderId.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseReference(string? reference, out int orderId)
    {
        orderId = 0;
        return reference is not null &&
               reference.StartsWith(ReferencePrefix, StringComparison.Ordinal) &&
               int.TryParse(reference[ReferencePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out orderId) &&
               orderId > 0;
    }

    public async Task<string?> TryStartCheckoutAsync(Order order, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            logger.LogWarning("Mercado Pago no está configurado: el pedido {OrderId} queda sin enlace de pago", order.Id);
            return null;
        }

        try
        {
            return await StartCheckoutAsync(order, cancellationToken);
        }
        catch (PaymentProviderException exception)
        {
            logger.LogError(exception, "No se pudo crear el pago del pedido {OrderId}", order.Id);
            return null;
        }
    }

    public async Task<string> StartCheckoutAsync(Order order, CancellationToken cancellationToken)
    {
        if (!gateway.IsConfigured)
        {
            throw new PaymentProviderException("Los pagos online no están disponibles en este momento.");
        }

        if (order.Status != OrderStatus.PendingPayment || order.PaymentExpiresAt is null || order.PaymentExpiresAt <= clock.UtcNow)
        {
            throw new ConflictException("Este pedido ya no se puede pagar.", ErrorCodes.NotPayable);
        }

        await ReleaseCheckoutAsync(order, cancellationToken);
        var payerEmail = order.ClientId is null
            ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == order.ClientId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);

        CreatedPreference preference;
        try
        {
            preference = await gateway.CreatePreferenceAsync(new PreferenceRequest(
                ReferenceFor(order.Id),
                new PreferenceItem($"pedido-{order.Id}", $"Pedido #{order.Id} de Cráneo Barbero", order.Total),
                order.PaymentExpiresAt.Value,
                payerEmail), cancellationToken);
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogError(exception, "Mercado Pago rechazó la preferencia del pedido {OrderId}", order.Id);
            throw new PaymentProviderException(AppointmentPayments.ProviderErrorMessage);
        }

        db.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Amount = order.Total,
            Method = PaymentMethod.MercadoPago,
            Status = TransactionStatus.Pending,
            PreferenceId = preference.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        return preference.CheckoutUrl;
    }

    public async Task<int?> ProcessAsync(GatewayPayment remote, CancellationToken cancellationToken)
    {
        if (!TryParseReference(remote.ExternalReference, out var orderId))
        {
            return null;
        }

        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            logger.LogWarning("El pago {PaymentId} apunta a un pedido inexistente ({OrderId})", remote.Id, orderId);
            return null;
        }

        var payment = await FindPaymentAsync(orderId, remote);
        var alreadyHandled = payment.ExternalId == remote.Id && payment.Status is TransactionStatus.Approved or TransactionStatus.Refunded;
        payment.ExternalId = remote.Id;

        if (remote.Status == "approved" && !alreadyHandled)
        {
            await ApproveAsync(order, payment, remote, cancellationToken);
            return orderId;
        }

        switch (remote.Status)
        {
            case "rejected" or "cancelled" when payment.Status == TransactionStatus.Pending:
                payment.Status = TransactionStatus.Rejected;
                break;
            case "refunded" or "charged_back":
                payment.Status = TransactionStatus.Refunded;
                payment.RefundedAt ??= clock.UtcNow;
                break;
        }

        await SaveIgnoringDuplicatesAsync(remote.Id, cancellationToken);
        return orderId;

        async Task<Payment> FindPaymentAsync(int id, GatewayPayment gatewayPayment)
        {
            var found = await db.Payments.FirstOrDefaultAsync(p => p.ExternalId == gatewayPayment.Id, cancellationToken)
                        ?? await db.Payments
                            .Where(p => p.OrderId == id && p.ExternalId == null && p.Status == TransactionStatus.Pending)
                            .OrderByDescending(p => p.Id)
                            .FirstOrDefaultAsync(cancellationToken);
            if (found is null)
            {
                found = new Payment { OrderId = id, Method = PaymentMethod.MercadoPago, Amount = Math.Max(gatewayPayment.Amount, 0.01m) };
                db.Payments.Add(found);
            }

            return found;
        }
    }

    public async Task<bool> RefundAsync(Order order, CancellationToken cancellationToken)
    {
        var payment = await db.Payments
            .Where(p => p.OrderId == order.Id && p.Status == TransactionStatus.Approved)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            return false;
        }

        if (payment.ExternalId is not null)
        {
            try
            {
                await gateway.RefundAsync(payment.ExternalId, cancellationToken);
            }
            catch (PaymentGatewayException exception)
            {
                logger.LogError(exception, "Falló el reembolso del pago {PaymentId} del pedido {OrderId}", payment.ExternalId, order.Id);
                throw new PaymentProviderException("No se pudo hacer el reembolso en Mercado Pago. Probá de nuevo en unos minutos.");
            }
        }

        payment.Status = TransactionStatus.Refunded;
        payment.RefundedAt = clock.UtcNow;
        return true;
    }

    public async Task ReleaseCheckoutAsync(Order order, CancellationToken cancellationToken)
    {
        var pending = await db.Payments
            .Where(p => p.OrderId == order.Id && p.Status == TransactionStatus.Pending && p.ExternalId == null && p.PreferenceId != null)
            .ToListAsync(cancellationToken);
        foreach (var payment in pending)
        {
            if (gateway.IsConfigured)
            {
                try
                {
                    await gateway.ExpirePreferenceAsync(payment.PreferenceId!, cancellationToken);
                }
                catch (PaymentGatewayException exception)
                {
                    logger.LogWarning(exception, "No se pudo anular la preferencia {PreferenceId}", payment.PreferenceId);
                }
            }

            payment.Status = TransactionStatus.Rejected;
        }
    }

    private async Task ApproveAsync(Order order, Payment payment, GatewayPayment remote, CancellationToken cancellationToken)
    {
        payment.Status = TransactionStatus.Approved;
        payment.Amount = remote.Amount > 0 ? remote.Amount : payment.Amount;
        payment.PaidAt = remote.ApprovedAt ?? clock.UtcNow;

        await using (var transaction = await ledger.BeginAsync(cancellationToken))
        {
            var payable = remote.Amount >= order.Total && order.ClientId is not null;
            if (payable && await transitions.TryMoveAsync(order, OrderStatus.PendingPayment, OrderStatus.Paid, cancellationToken))
            {
                order.PaymentExpiresAt = null;
                try
                {
                    foreach (var item in order.Items.OrderBy(i => i.ProductId))
                    {
                        var result = await ledger.ApplyAsync(new StockChange(
                            item.ProductId,
                            StockMovementType.Sale,
                            -item.Quantity,
                            order.ClientId!.Value,
                            clock.UtcNow,
                            OrderId: order.Id), cancellationToken);
                        item.UnitCost = result.CostBefore;
                    }
                }
                catch (AppException exception) when (exception.Code is ErrorCodes.InsufficientStock or ErrorCodes.ProductUnavailable)
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                    }

                    db.ChangeTracker.Clear();
                    logger.LogWarning("El pedido {OrderId} se pagó pero ya no hay stock: se cancela y se reembolsa", order.Id);
                    await CancelForMissingStockAsync(order.Id, remote, cancellationToken);
                    return;
                }

                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return;
            }

            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        if (await db.Payments.AsNoTracking().AnyAsync(p => p.ExternalId == remote.Id && p.Status != TransactionStatus.Pending, cancellationToken))
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("El pago {PaymentId} del pedido {OrderId} ya había sido procesado", remote.Id, order.Id);
            return;
        }

        logger.LogWarning(
            "Pago {PaymentId} aprobado para el pedido {OrderId} en estado {Status} (importe {Amount}): se reembolsa",
            remote.Id, order.Id, order.Status, remote.Amount);
        await RefundRemoteAsync(payment, remote.Id, cancellationToken);
        await SaveIgnoringDuplicatesAsync(remote.Id, cancellationToken);
    }

    private async Task CancelForMissingStockAsync(int orderId, GatewayPayment remote, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, cancellationToken);
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.ExternalId == remote.Id, cancellationToken)
                      ?? await db.Payments
                          .Where(p => p.OrderId == orderId && p.ExternalId == null && p.Status == TransactionStatus.Pending)
                          .OrderByDescending(p => p.Id)
                          .FirstOrDefaultAsync(cancellationToken);
        if (payment is null)
        {
            payment = new Payment { OrderId = orderId, Method = PaymentMethod.MercadoPago };
            db.Payments.Add(payment);
        }

        payment.ExternalId = remote.Id;
        payment.Amount = remote.Amount > 0 ? remote.Amount : Math.Max(order.Total, 0.01m);
        payment.Status = TransactionStatus.Approved;
        payment.PaidAt = remote.ApprovedAt ?? clock.UtcNow;

        if (!await transitions.TryMoveAsync(order, OrderStatus.PendingPayment, OrderStatus.Cancelled, cancellationToken))
        {
            db.ChangeTracker.Clear();
            return;
        }

        order.PaymentExpiresAt = null;
        order.CancellationReason = OrderLimits.OutOfStockReason;
        order.RefundStatus = await RefundRemoteAsync(payment, remote.Id, cancellationToken) ? RefundStatus.Refunded : RefundStatus.None;
        await SaveIgnoringDuplicatesAsync(remote.Id, cancellationToken);
    }

    private async Task<bool> RefundRemoteAsync(Payment payment, string remoteId, CancellationToken cancellationToken)
    {
        try
        {
            await gateway.RefundAsync(remoteId, cancellationToken);
            payment.Status = TransactionStatus.Refunded;
            payment.RefundedAt = clock.UtcNow;
            return true;
        }
        catch (PaymentGatewayException exception)
        {
            logger.LogError(exception, "No se pudo reembolsar el pago {PaymentId}; revisarlo a mano en Mercado Pago", remoteId);
            return false;
        }
    }

    private async Task SaveIgnoringDuplicatesAsync(string remoteId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
                                                   || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("El pago {PaymentId} ya había sido procesado", remoteId);
        }
    }
}
