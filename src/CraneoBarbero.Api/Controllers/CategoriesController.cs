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
[Route("categories")]
public sealed class CategoriesController(AppDbContext db) : ControllerBase
{
    [Authorize(Policy = Policies.ClientOrAdmin)]
    [HttpGet]
    public async Task<IReadOnlyList<CategoryResponse>> List(CancellationToken cancellationToken)
    {
        var categories = await db.ProductCategories.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return categories.Select(CategoryResponse.From).ToList();
    }

    [Authorize(Policy = Policies.ClientOrAdmin)]
    [HttpGet("{id}")]
    public async Task<CategoryResponse> Get(int id, CancellationToken cancellationToken) =>
        CategoryResponse.From(await FindAsync(id, cancellationToken));

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<CategoryResponse>> Create(CategoryRequest request, CancellationToken cancellationToken)
    {
        var category = new ProductCategory { Name = request.Name!.Trim() };
        await EnsureUniqueNameAsync(category.Name, null, cancellationToken);
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = category.Id }, CategoryResponse.From(category));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<CategoryResponse> Update(int id, CategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await FindAsync(id, cancellationToken);
        var name = request.Name!.Trim();
        await EnsureUniqueNameAsync(name, id, cancellationToken);
        category.Name = name;
        await db.SaveChangesAsync(cancellationToken);
        return CategoryResponse.From(category);
    }

    private async Task EnsureUniqueNameAsync(string name, int? exceptId, CancellationToken cancellationToken)
    {
        var lowered = name.ToLower();
        var exists = await db.ProductCategories.AnyAsync(
            c => c.Name.ToLower() == lowered && (exceptId == null || c.Id != exceptId),
            cancellationToken);
        if (exists)
        {
            throw new ConflictException("Ya existe una categoría con ese nombre.", ErrorCodes.CategoryExists);
        }
    }

    private async Task<ProductCategory> FindAsync(int id, CancellationToken cancellationToken) =>
        await db.ProductCategories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw new NotFoundException("No se encontró la categoría.");
}
