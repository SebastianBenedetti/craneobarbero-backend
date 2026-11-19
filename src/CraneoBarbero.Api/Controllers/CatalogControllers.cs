using CraneoBarbero.Api.Contracts;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("branches")]
public sealed class BranchesController(AppDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IReadOnlyList<BranchResponse>> List(CancellationToken cancellationToken) =>
        await db.Branches.AsNoTracking()
            .Where(b => b.IsActive)
            .OrderBy(b => b.Id)
            .Select(b => new BranchResponse(b.Id, b.Name, b.Address, b.Phone, b.ImageUrl))
            .ToListAsync(cancellationToken);
}

[ApiController]
[Route("cuts")]
public sealed class CutsController(AppDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IReadOnlyList<CutResponse>> List(CancellationToken cancellationToken) =>
        await db.Cuts.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Id)
            .Select(c => new CutResponse(c.Id, c.Name, c.Price, c.Description, c.DurationMinutes))
            .ToListAsync(cancellationToken);
}

[ApiController]
[Route("barbers")]
public sealed class BarbersController(AppDbContext db) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IReadOnlyList<PublicBarberResponse>> List(CancellationToken cancellationToken) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Barber && u.IsActive && u.Branch!.IsActive)
            .OrderBy(u => u.Name)
            .Select(u => new PublicBarberResponse(
                u.Id, u.Name, u.ImageUrl, u.BranchId!.Value, new BranchSummary(u.Branch!.Id, u.Branch.Name), u.StartDate))
            .ToListAsync(cancellationToken);
}
