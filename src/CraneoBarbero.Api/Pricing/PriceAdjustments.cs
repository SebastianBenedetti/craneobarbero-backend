using System.Globalization;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Store;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Pricing;

public sealed class PriceAdjustments(AppDbContext db, PriceHistoryWriter history, BusinessClock clock)
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public async Task<PriceAdjustmentPreview> PreviewAsync(PriceAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var (products, cuts, description) = await LoadScopeAsync(request, tracked: false, cancellationToken);
        var items = BuildItems(products, cuts, request);
        return ToPreview(request, description, items);
    }

    public async Task<PriceAdjustmentResponse> ApplyAsync(PriceAdjustmentRequest request, int userId, CancellationToken cancellationToken)
    {
        if (request.Confirm != true)
        {
            throw new BadRequestException("Confirmá el ajuste para aplicarlo.", ErrorCodes.ConfirmationRequired);
        }

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var (products, cuts, description) = await LoadScopeAsync(request, tracked: true, cancellationToken);
        var items = BuildItems(products, cuts, request);
        if (items.Count == 0)
        {
            throw new ConflictException("No hay precios para modificar con ese alcance.", ErrorCodes.NothingToAdjust);
        }

        var adjustment = new PriceAdjustment
        {
            AppliedById = userId,
            AppliedAt = clock.UtcNow,
            Source = request.Source ?? PriceAdjustmentSource.Manual,
            Percentage = request.Percentage!.Value,
            PeriodFrom = ParsePeriod(request.PeriodFrom),
            PeriodTo = ParsePeriod(request.PeriodTo),
            ScopeType = request.ScopeType!,
            ScopeDescription = description,
            RoundingMode = request.Rounding!,
            IncludesCuts = request.IncludeCuts == true,
            ItemsCount = items.Count,
        };
        db.PriceAdjustments.Add(adjustment);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var item in items)
        {
            if (item.EntityType == PriceEntityType.Product)
            {
                products.Single(p => p.Id == item.EntityId).SalePrice = item.NewPrice;
            }
            else
            {
                cuts.Single(c => c.Id == item.EntityId).Price = item.NewPrice;
            }

            history.Record(item.EntityType, item.EntityId, PriceField.Price, item.CurrentPrice, item.NewPrice,
                PriceChangeReason.Inflation, userId, adjustment.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return (await ListAsync(cancellationToken)).Single(a => a.Id == adjustment.Id);
    }

    public async Task<PriceAdjustmentRevertResponse> RevertAsync(int id, int userId, CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var adjustment = await db.PriceAdjustments.FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
                         ?? throw new NotFoundException("No se encontró el ajuste.");
        if (adjustment.RevertedAt is not null)
        {
            throw new ConflictException("Este ajuste ya se revirtió.", ErrorCodes.AlreadyReverted);
        }

        if (await LastRevertableIdAsync(cancellationToken) != id)
        {
            throw new ConflictException("Solo se puede revertir el último ajuste aplicado.", ErrorCodes.NotLastAdjustment);
        }

        var rows = await db.PriceHistory.AsNoTracking()
            .Where(h => h.AdjustmentId == id && h.Reason == PriceChangeReason.Inflation && h.Field == PriceField.Price)
            .ToListAsync(cancellationToken);
        var productIds = rows.Where(r => r.EntityType == PriceEntityType.Product).Select(r => r.EntityId).ToList();
        var cutIds = rows.Where(r => r.EntityType == PriceEntityType.Cut).Select(r => r.EntityId).ToList();
        var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var cuts = await db.Cuts.Where(c => cutIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);

        var restored = 0;
        var skipped = new List<SkippedRevertItem>();
        foreach (var row in rows)
        {
            var (name, current) = row.EntityType == PriceEntityType.Product
                ? products.TryGetValue(row.EntityId, out var product) ? (product.Name, product.SalePrice) : ((string?)null, 0m)
                : cuts.TryGetValue(row.EntityId, out var cut) ? (cut.Name, cut.Price) : ((string?)null, 0m);
            if (name is null)
            {
                continue;
            }

            if (current != row.NewPrice)
            {
                skipped.Add(new SkippedRevertItem(row.EntityType, row.EntityId, name, row.NewPrice, current));
                continue;
            }

            if (row.EntityType == PriceEntityType.Product)
            {
                products[row.EntityId].SalePrice = row.OldPrice;
            }
            else
            {
                cuts[row.EntityId].Price = row.OldPrice;
            }

            history.Record(row.EntityType, row.EntityId, PriceField.Price, current, row.OldPrice, PriceChangeReason.Revert, userId, id);
            restored++;
        }

        adjustment.RevertedAt = clock.UtcNow;
        adjustment.RevertedById = userId;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new PriceAdjustmentRevertResponse(id, restored, skipped);
    }

    public async Task<IReadOnlyList<PriceAdjustmentResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var lastId = await LastRevertableIdAsync(cancellationToken);
        var rows = await db.PriceAdjustments.AsNoTracking()
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Take(50)
            .Select(a => new
            {
                Adjustment = a,
                AppliedBy = db.Users.Where(u => u.Id == a.AppliedById).Select(u => new PriceAdjustmentPerson(u.Id, u.Name)).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new PriceAdjustmentResponse(
                r.Adjustment.Id,
                r.Adjustment.AppliedAt,
                r.AppliedBy,
                r.Adjustment.Source,
                r.Adjustment.Percentage,
                FormatPeriod(r.Adjustment.PeriodFrom),
                FormatPeriod(r.Adjustment.PeriodTo),
                r.Adjustment.ScopeType,
                r.Adjustment.ScopeDescription,
                r.Adjustment.RoundingMode,
                r.Adjustment.IncludesCuts,
                r.Adjustment.ItemsCount,
                r.Adjustment.RevertedAt,
                r.Adjustment.Id == lastId))
            .ToList();
    }

    public async Task<PagedResponse<PriceHistoryResponse>> HistoryAsync(PriceHistoryQuery query, CancellationToken cancellationToken)
    {
        var rows = db.PriceHistory.AsNoTracking();
        if (query.EntityType is { } type)
        {
            rows = rows.Where(h => h.EntityType == type);
        }

        if (query.EntityId is { } entityId)
        {
            rows = rows.Where(h => h.EntityId == entityId);
        }

        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? AppointmentLimits.DefaultPageSize;
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderByDescending(h => h.ChangedAt)
            .ThenByDescending(h => h.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new PriceHistoryResponse(
                h.Id,
                h.EntityType,
                h.EntityId,
                h.Field,
                h.OldPrice,
                h.NewPrice,
                h.Reason,
                h.AdjustmentId,
                db.Users.Where(u => u.Id == h.ChangedById).Select(u => new PriceAdjustmentPerson(u.Id, u.Name)).FirstOrDefault(),
                h.ChangedAt))
            .ToListAsync(cancellationToken);
        return new PagedResponse<PriceHistoryResponse>(items, page, pageSize, total);
    }

    private async Task<int?> LastRevertableIdAsync(CancellationToken cancellationToken)
    {
        var last = await db.PriceAdjustments.AsNoTracking()
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, a.RevertedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null)
        {
            return null;
        }

        if (last.RevertedAt is null)
        {
            return last.Id;
        }

        return await db.PriceAdjustments.AsNoTracking()
            .Where(a => a.RevertedAt == null)
            .OrderByDescending(a => a.AppliedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => (int?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(List<Product> Products, List<Cut> Cuts, string Description)> LoadScopeAsync(
        PriceAdjustmentRequest request, bool tracked, CancellationToken cancellationToken)
    {
        var productQuery = tracked ? db.Products : db.Products.AsNoTracking();
        var cutQuery = tracked ? db.Cuts : db.Cuts.AsNoTracking();
        List<Product> products;
        string description;

        switch (request.ScopeType)
        {
            case AdjustmentScopes.Category:
                var categoryId = request.CategoryId!.Value;
                var category = await db.ProductCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken)
                               ?? throw new BadRequestException("La categoría elegida no existe.", ErrorCodes.CategoryUnavailable);
                products = await productQuery.Where(p => p.IsActive && p.CategoryId == categoryId).ToListAsync(cancellationToken);
                description = $"Categoría {category.Name}";
                break;
            case AdjustmentScopes.Selection:
                var ids = (request.ProductIds ?? []).Distinct().ToList();
                products = await productQuery.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
                if (products.Count != ids.Count)
                {
                    throw new BadRequestException("Alguno de los productos elegidos no existe.", ErrorCodes.ProductUnavailable);
                }

                description = ids.Count == 1 ? "1 producto seleccionado" : $"{ids.Count} productos seleccionados";
                break;
            default:
                products = await productQuery.Where(p => p.IsActive).ToListAsync(cancellationToken);
                description = "Todos los productos";
                break;
        }

        var cuts = request.IncludeCuts == true
            ? await cutQuery.Where(c => c.IsActive).ToListAsync(cancellationToken)
            : [];
        if (request.IncludeCuts == true)
        {
            description += " y los servicios";
        }

        return (products, cuts, description);
    }

    private static List<PriceAdjustmentItem> BuildItems(List<Product> products, List<Cut> cuts, PriceAdjustmentRequest request)
    {
        var percentage = request.Percentage!.Value;
        var rounding = request.Rounding!;
        var items = products
            .OrderBy(p => p.Name)
            .Select(p =>
            {
                var price = AdjustmentRounding.Apply(p.SalePrice, percentage, rounding);
                return new PriceAdjustmentItem(PriceEntityType.Product, p.Id, p.Name, p.SalePrice, price, price - p.SalePrice,
                    p.Cost, StockMath.MarginPercent(price, p.Cost));
            })
            .Concat(cuts.OrderBy(c => c.Name).Select(c =>
            {
                var price = AdjustmentRounding.Apply(c.Price, percentage, rounding);
                return new PriceAdjustmentItem(PriceEntityType.Cut, c.Id, c.Name, c.Price, price, price - c.Price, null, null);
            }))
            .Where(i => i.NewPrice != i.CurrentPrice && i.NewPrice <= StoreLimits.MaxAmount)
            .ToList();
        return items;
    }

    private static PriceAdjustmentPreview ToPreview(PriceAdjustmentRequest request, string description, List<PriceAdjustmentItem> items)
    {
        var percentage = request.Percentage!.Value;
        var text = percentage.ToString("0.####", Spanish);
        var message = items.Count == 1
            ? $"Se modificará 1 precio con un aumento de {text}%."
            : $"Se modificarán {items.Count} precios con un aumento de {text}%.";
        return new PriceAdjustmentPreview(percentage, request.Rounding!, description, items.Count, message, items);
    }

    private static DateOnly? ParsePeriod(string? value) =>
        value is not null && DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
            ? month
            : null;

    private static string? FormatPeriod(DateOnly? value) => value?.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
