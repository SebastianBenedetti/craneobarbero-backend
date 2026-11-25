using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Store;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("suppliers")]
[Authorize(Policy = Policies.Admin)]
public sealed class SuppliersController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<SupplierResponse>> List([FromQuery] bool? active, CancellationToken cancellationToken)
    {
        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(s => active == null || s.IsActive == active)
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        return suppliers.Select(SupplierResponse.From).ToList();
    }

    [HttpGet("{id}")]
    public async Task<SupplierResponse> Get(int id, CancellationToken cancellationToken) =>
        SupplierResponse.From(await FindAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<SupplierResponse>> Create(SupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = new Supplier();
        Apply(supplier, request);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = supplier.Id }, SupplierResponse.From(supplier));
    }

    [HttpPut("{id}")]
    public async Task<SupplierResponse> Update(int id, SupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = await FindAsync(id, cancellationToken);
        Apply(supplier, request);
        await db.SaveChangesAsync(cancellationToken);
        return SupplierResponse.From(supplier);
    }

    private async Task<Supplier> FindAsync(int id, CancellationToken cancellationToken) =>
        await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw new NotFoundException("No se encontró el proveedor.");

    private static void Apply(Supplier supplier, SupplierRequest request)
    {
        supplier.Name = request.Name!.Trim();
        supplier.ContactName = AccountRules.TrimToNull(request.ContactName);
        supplier.Phone = AccountRules.TrimToNull(request.Phone);
        supplier.Email = AccountRules.TrimToNull(request.Email)?.ToLowerInvariant();
        supplier.TaxId = AccountRules.TrimToNull(request.TaxId);
        supplier.Notes = AccountRules.TrimToNull(request.Notes);
        if (request.IsActive is { } isActive)
        {
            supplier.IsActive = isActive;
        }
    }
}
