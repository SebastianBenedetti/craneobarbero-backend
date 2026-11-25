using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CraneoBarbero.Api.Store;

public sealed record StockChange(
    int ProductId,
    StockMovementType Type,
    int Quantity,
    int UserId,
    DateTimeOffset OccurredAt,
    decimal? UnitCost = null,
    int? SupplierId = null,
    string? DocumentNumber = null,
    int? OrderId = null,
    string? Reason = null);

public sealed record StockChangeResult(StockMovement Movement, string ProductName, decimal CostBefore);

public sealed class StockLedger(AppDbContext db, BusinessClock clock)
{
    public async Task<IDbContextTransaction?> BeginAsync(CancellationToken cancellationToken) =>
        db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;

    public async Task<StockChangeResult> ApplyAsync(StockChange change, CancellationToken cancellationToken)
    {
        var quantity = change.Quantity;
        int balance;
        string name;
        decimal cost;

        if (db.Database.IsRelational())
        {
            var now = clock.UtcNow;
            var updated = await db.Products
                .Where(p => p.Id == change.ProductId && p.Stock + quantity >= 0)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(p => p.Stock, p => p.Stock + quantity)
                    .SetProperty(p => p.UpdatedAt, now), cancellationToken);
            if (updated == 0)
            {
                await ThrowUnavailableAsync(change.ProductId, cancellationToken);
            }

            var row = await db.Products.AsNoTracking()
                .Where(p => p.Id == change.ProductId)
                .Select(p => new { p.Stock, p.Name, p.Cost })
                .SingleAsync(cancellationToken);
            (balance, name, cost) = (row.Stock, row.Name, row.Cost);
        }
        else
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == change.ProductId, cancellationToken);
            if (product is null || product.Stock + quantity < 0)
            {
                await ThrowUnavailableAsync(change.ProductId, cancellationToken);
            }

            product!.Stock += quantity;
            (balance, name, cost) = (product.Stock, product.Name, product.Cost);
        }

        var movement = new StockMovement
        {
            ProductId = change.ProductId,
            Type = change.Type,
            Quantity = quantity,
            UnitCost = change.UnitCost,
            SupplierId = change.SupplierId,
            DocumentNumber = change.DocumentNumber,
            OccurredAt = change.OccurredAt,
            BalanceAfter = balance,
            OrderId = change.OrderId,
            UserId = change.UserId,
            Reason = change.Reason,
        };
        db.StockMovements.Add(movement);
        return new StockChangeResult(movement, name, cost);
    }

    public async Task SetCostAsync(int productId, decimal cost, CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var now = clock.UtcNow;
            await db.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(set => set
                .SetProperty(p => p.Cost, cost)
                .SetProperty(p => p.UpdatedAt, now), cancellationToken);
            return;
        }

        var product = await db.Products.SingleAsync(p => p.Id == productId, cancellationToken);
        product.Cost = cost;
    }

    private async Task ThrowUnavailableAsync(int productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Name, p.Stock })
            .FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            throw new BadRequestException("El producto elegido no existe.", ErrorCodes.ProductUnavailable);
        }

        throw new ConflictException(
            $"No hay stock suficiente de {product.Name}: quedan {product.Stock}. El stock no puede quedar negativo.",
            ErrorCodes.InsufficientStock);
    }
}
