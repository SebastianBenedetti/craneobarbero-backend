using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("products")]
public sealed class ProductsController(AppDbContext db, BusinessClock clock) : ControllerBase
{
    [Authorize(Policy = Policies.ClientOrAdmin)]
    [HttpGet]
    public async Task<IReadOnlyList<object>> List([FromQuery] ProductListQuery query, CancellationToken cancellationToken)
    {
        var isAdmin = User.IsInRole(Roles.Admin);
        var products = WithReferences();
        if (query.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        if (!isAdmin)
        {
            products = products.Where(p => p.IsActive);
        }
        else
        {
            if (query.Active is { } active)
            {
                products = products.Where(p => p.IsActive == active);
            }

            if (query.LowStock == true)
            {
                products = products.Where(p => p.Stock <= p.MinStock);
            }
        }

        var term = query.Q?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            products = Matching(products, term, includeSku: isAdmin);
        }

        var rows = await products.OrderBy(p => p.Name).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        return isAdmin
            ? rows.Select(p => (object)AdminProductResponse.From(p)).ToList()
            : rows.Select(p => (object)ClientProductResponse.From(p)).ToList();
    }

    [Authorize(Policy = Policies.ClientOrAdmin)]
    [HttpGet("{id}")]
    public async Task<object> Get(int id, CancellationToken cancellationToken)
    {
        var product = await WithReferences().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        var isAdmin = User.IsInRole(Roles.Admin);
        if (product is null || (!product.IsActive && !isAdmin))
        {
            throw new NotFoundException("No se encontró el producto.");
        }

        return isAdmin ? AdminProductResponse.From(product) : ClientProductResponse.From(product);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<AdminProductResponse>> Create(ProductRequest request, CancellationToken cancellationToken)
    {
        var product = new Product { Cost = request.Cost ?? 0m };
        await ApplyAsync(product, request, cancellationToken);
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, await LoadAsync(product.Id, cancellationToken));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<AdminProductResponse> Update(int id, ProductRequest request, CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("No se encontró el producto.");
        var oldPrice = product.SalePrice;
        var oldCost = product.Cost;

        await ApplyAsync(product, request, cancellationToken);
        if (request.Cost is { } cost)
        {
            product.Cost = cost;
        }

        var userId = User.GetRequiredUserId();
        RecordChange(product.Id, PriceField.Price, oldPrice, product.SalePrice, userId);
        RecordChange(product.Id, PriceField.Cost, oldCost, product.Cost, userId);
        await db.SaveChangesAsync(cancellationToken);
        return await LoadAsync(id, cancellationToken);
    }

    private IQueryable<Product> WithReferences() =>
        db.Products.AsNoTracking().Include(p => p.Category).Include(p => p.DefaultSupplier);

    private async Task<AdminProductResponse> LoadAsync(int id, CancellationToken cancellationToken) =>
        AdminProductResponse.From(await WithReferences().SingleAsync(p => p.Id == id, cancellationToken));

    private IQueryable<Product> Matching(IQueryable<Product> products, string term, bool includeSku)
    {
        var lower = term.ToLowerInvariant();
        if (db.Database.IsRelational())
        {
            var pattern = $"%{lower.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            return products.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") ||
                                       (includeSku && p.Sku != null && EF.Functions.ILike(p.Sku, pattern, "\\")));
        }

        return products.Where(p => p.Name.ToLower().Contains(lower) ||
                                   (includeSku && p.Sku != null && p.Sku.ToLower().Contains(lower)));
    }

    private void RecordChange(int productId, PriceField field, decimal oldValue, decimal newValue, int userId)
    {
        if (oldValue == newValue)
        {
            return;
        }

        db.PriceHistory.Add(new PriceHistory
        {
            EntityType = PriceEntityType.Product,
            EntityId = productId,
            Field = field,
            OldPrice = oldValue,
            NewPrice = newValue,
            Reason = PriceChangeReason.Manual,
            ChangedById = userId,
            ChangedAt = clock.UtcNow,
        });
    }

    private async Task ApplyAsync(Product product, ProductRequest request, CancellationToken cancellationToken)
    {
        var categoryId = request.CategoryId!.Value;
        if (!await db.ProductCategories.AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            throw new BadRequestException("La categoría elegida no existe.", ErrorCodes.CategoryUnavailable);
        }

        var supplierId = request.DefaultSupplierId;
        if (supplierId is not null && supplierId != product.DefaultSupplierId &&
            !await db.Suppliers.AnyAsync(s => s.Id == supplierId && s.IsActive, cancellationToken))
        {
            throw new BadRequestException("El proveedor elegido no existe o está desactivado.", ErrorCodes.SupplierUnavailable);
        }

        var sku = AccountRules.TrimToNull(request.Sku);
        if (sku is not null && sku != product.Sku &&
            await db.Products.AnyAsync(p => p.Sku == sku && p.Id != product.Id, cancellationToken))
        {
            throw new ConflictException("Ya existe otro producto con ese código interno (SKU).", ErrorCodes.SkuInUse);
        }

        product.CategoryId = categoryId;
        product.Name = request.Name!.Trim();
        product.Description = AccountRules.TrimToNull(request.Description);
        product.Sku = sku;
        product.Barcode = AccountRules.TrimToNull(request.Barcode);
        product.SalePrice = request.SalePrice!.Value;
        product.MinStock = request.MinStock!.Value;
        product.ReorderQuantity = request.ReorderQuantity;
        product.DefaultSupplierId = supplierId;
        product.ImageUrl = AccountRules.TrimToNull(request.ImageUrl);
        if (request.IsActive is { } isActive)
        {
            product.IsActive = isActive;
        }
    }
}
