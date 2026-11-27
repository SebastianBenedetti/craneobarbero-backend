using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Pricing;
using CraneoBarbero.Api.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("stock")]
[Authorize(Policy = Policies.Admin)]
public sealed class StockController(AppDbContext db, StockLedger ledger, PriceHistoryWriter history, BusinessClock clock) : ControllerBase
{
    [HttpPost("movements")]
    public async Task<ActionResult<StockMovementResponse>> Adjust(StockAdjustmentRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await ledger.BeginAsync(cancellationToken);
        var result = await ledger.ApplyAsync(new StockChange(
            request.ProductId!.Value,
            StockMovementType.Adjustment,
            request.Quantity!.Value,
            User.GetRequiredUserId(),
            clock.UtcNow,
            Reason: AccountRules.TrimToNull(request.Reason) ?? "Ajuste manual"), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var movement = await Project(db.StockMovements.Where(m => m.Id == result.Movement.Id)).SingleAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, movement);
    }

    [HttpPost("purchases")]
    public async Task<ActionResult<PurchaseResponse>> Purchase(PurchaseRequest request, CancellationToken cancellationToken)
    {
        StockMovementPersonRef? supplier = null;
        if (request.SupplierId is { } supplierId)
        {
            supplier = await db.Suppliers.AsNoTracking()
                .Where(s => s.Id == supplierId && s.IsActive)
                .Select(s => new StockMovementPersonRef(s.Id, s.Name))
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new BadRequestException("El proveedor elegido no existe o está desactivado.", ErrorCodes.SupplierUnavailable);
        }

        var now = clock.UtcNow;
        var date = request.Date!.Value;
        var occurredAt = date == clock.Today ? now : clock.ToUtc(date, new TimeOnly(12, 0));
        var documentNumber = AccountRules.TrimToNull(request.DocumentNumber);
        var userId = User.GetRequiredUserId();

        await using var transaction = await ledger.BeginAsync(cancellationToken);
        var lines = new List<(StockChangeResult Result, PurchaseItemRequest Item)>();
        foreach (var item in request.Items!.OrderBy(i => i.ProductId))
        {
            var unitCost = item.UnitCost!.Value;
            var result = await ledger.ApplyAsync(new StockChange(
                item.ProductId!.Value,
                StockMovementType.In,
                item.Quantity!.Value,
                userId,
                occurredAt,
                UnitCost: unitCost,
                SupplierId: supplier?.Id,
                DocumentNumber: documentNumber), cancellationToken);
            if (result.CostBefore != unitCost)
            {
                await ledger.SetCostAsync(item.ProductId.Value, unitCost, cancellationToken);
                history.Record(PriceEntityType.Product, item.ProductId.Value, PriceField.Cost, result.CostBefore, unitCost, PriceChangeReason.Purchase, userId);
            }

            lines.Add((result, item));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var items = request.Items!
            .Select(item => lines.Single(l => l.Item.ProductId == item.ProductId))
            .Select(l => new PurchaseItemResponse(
                l.Result.Movement.Id,
                l.Result.Movement.ProductId,
                l.Result.ProductName,
                l.Result.Movement.Quantity,
                l.Result.Movement.UnitCost!.Value,
                l.Result.CostBefore,
                l.Result.Movement.UnitCost.Value,
                l.Result.Movement.BalanceAfter))
            .ToList();
        var total = items.Sum(i => i.Quantity * i.UnitCost);
        return StatusCode(StatusCodes.Status201Created, new PurchaseResponse(supplier, documentNumber, occurredAt, total, items));
    }

    [HttpGet("movements")]
    public async Task<PagedResponse<StockMovementResponse>> List([FromQuery] StockMovementQuery query, CancellationToken cancellationToken)
    {
        var movements = db.StockMovements.AsNoTracking();
        if (query.ProductId is { } productId)
        {
            movements = movements.Where(m => m.ProductId == productId);
        }

        if (query.Type is { } type)
        {
            movements = movements.Where(m => m.Type == type);
        }

        if (query.SupplierId is { } supplierId)
        {
            movements = movements.Where(m => m.SupplierId == supplierId);
        }

        if (query.From is { } from)
        {
            var start = clock.ToUtc(from, TimeOnly.MinValue);
            movements = movements.Where(m => m.OccurredAt >= start);
        }

        if (query.To is { } to)
        {
            var end = clock.ToUtc(to.AddDays(1), TimeOnly.MinValue);
            movements = movements.Where(m => m.OccurredAt < end);
        }

        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? AppointmentLimits.DefaultPageSize;
        var total = await movements.CountAsync(cancellationToken);
        var ids = await movements
            .OrderByDescending(m => m.OccurredAt)
            .ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        var rows = await Project(db.StockMovements.Where(m => ids.Contains(m.Id))).ToListAsync(cancellationToken);
        var items = ids.Select(id => rows.Single(r => r.Id == id)).ToList();
        return new PagedResponse<StockMovementResponse>(items, page, pageSize, total);
    }

    [HttpGet("valuation")]
    public async Task<ValuationResponse> Valuation(CancellationToken cancellationToken)
    {
        var products = await db.Products.AsNoTracking()
            .Include(p => p.Category)
            .Where(p => p.IsActive || p.Stock > 0)
            .ToListAsync(cancellationToken);
        var items = products
            .Select(p => new ValuationItem(
                p.Id,
                p.Name,
                new ProductCategoryRef(p.Category!.Id, p.Category.Name),
                p.IsActive,
                p.Stock,
                p.Cost,
                p.Stock * p.Cost,
                p.SalePrice,
                p.Stock * p.SalePrice,
                StockMath.Margin(p.SalePrice, p.Cost),
                StockMath.MarginPercent(p.SalePrice, p.Cost)))
            .OrderByDescending(i => i.CostValue)
            .ThenBy(i => i.Name)
            .ToList();
        return new ValuationResponse(
            items.Sum(i => i.Stock),
            items.Sum(i => i.CostValue),
            items.Sum(i => i.SaleValue),
            items);
    }

    [HttpGet("reorder")]
    public async Task<ReorderResponse> Reorder(CancellationToken cancellationToken)
    {
        var products = await db.Products.AsNoTracking()
            .Include(p => p.DefaultSupplier)
            .Where(p => p.IsActive && p.Stock <= p.MinStock)
            .ToListAsync(cancellationToken);
        var items = products
            .OrderBy(p => p.DefaultSupplier == null)
            .ThenBy(p => p.DefaultSupplier?.Name)
            .ThenBy(p => p.Name)
            .Select(p => new ReorderItem(
                p.Id,
                p.Name,
                p.Sku,
                p.Stock,
                p.MinStock,
                p.ReorderQuantity,
                p.Cost,
                p.ReorderQuantity * p.Cost,
                p.DefaultSupplier is null ? null : new ProductSupplierRef(p.DefaultSupplier.Id, p.DefaultSupplier.Name)))
            .ToList();
        return new ReorderResponse(items.Count, items.Sum(i => i.EstimatedCost ?? 0m), items);
    }

    [HttpGet("alerts")]
    public async Task<StockAlertsResponse> Alerts(CancellationToken cancellationToken)
    {
        var items = await db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.Stock <= p.MinStock)
            .OrderBy(p => p.Stock)
            .ThenBy(p => p.Name)
            .Select(p => new StockAlertItem(p.Id, p.Name, p.Stock, p.MinStock))
            .ToListAsync(cancellationToken);
        return new StockAlertsResponse(items.Count, items);
    }

    private IQueryable<StockMovementResponse> Project(IQueryable<StockMovement> source) =>
        source.AsNoTracking().Select(m => new StockMovementResponse(
            m.Id,
            m.ProductId,
            m.Product!.Name,
            m.Type,
            m.Quantity,
            m.UnitCost,
            db.Suppliers.Where(s => s.Id == m.SupplierId).Select(s => new StockMovementPersonRef(s.Id, s.Name)).FirstOrDefault(),
            m.DocumentNumber,
            m.OccurredAt,
            m.BalanceAfter,
            m.OrderId,
            db.Users.Where(u => u.Id == m.UserId).Select(u => new StockMovementPersonRef(u.Id, u.Name)).FirstOrDefault(),
            m.Reason));
}
