using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("stock")]
[Authorize(Policy = Policies.Admin)]
public sealed class StockController(AppDbContext db, StockLedger ledger, BusinessClock clock) : ControllerBase
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
            Reason: request.Reason!.Trim()), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var movement = await Project(db.StockMovements.Where(m => m.Id == result.Movement.Id)).SingleAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, movement);
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

    private IQueryable<StockMovementResponse> Project(IQueryable<Domain.Entities.StockMovement> source) =>
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
