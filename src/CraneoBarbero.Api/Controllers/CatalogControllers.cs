using System.Linq.Expressions;
using System.Security.Claims;
using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Catalog;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Contracts;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

internal static class CatalogAccess
{
    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);

    public static void EnsureAdminWhen(this ClaimsPrincipal user, bool condition)
    {
        if (!condition || user.IsAdmin())
        {
            return;
        }

        if (user.Identity?.IsAuthenticated != true)
        {
            throw new UnauthorizedException(ErrorMessages.Unauthorized);
        }

        throw new ForbiddenException();
    }
}

[ApiController]
[Route("branches")]
public sealed class BranchesController(AppDbContext db, FutureAppointments futureAppointments) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IReadOnlyList<BranchResponse>> List([FromQuery] bool includeInactive, CancellationToken cancellationToken)
    {
        User.EnsureAdminWhen(includeInactive);
        var branches = await db.Branches.AsNoTracking()
            .Where(b => includeInactive || b.IsActive)
            .OrderBy(b => b.Id)
            .ToListAsync(cancellationToken);
        return branches.Select(BranchResponse.From).ToList();
    }

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<BranchResponse> Get(int id, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (branch is null || (!branch.IsActive && !User.IsAdmin()))
        {
            throw new NotFoundException("No se encontró la sucursal.");
        }

        return BranchResponse.From(branch);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<BranchResponse>> Create(BranchRequest request, CancellationToken cancellationToken)
    {
        var branch = new Branch();
        Apply(branch, request);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = branch.Id }, BranchResponse.From(branch));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<BranchResponse> Update(int id, BranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        Apply(branch, request);
        await db.SaveChangesAsync(cancellationToken);
        return BranchResponse.From(branch);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/deactivate")]
    public async Task<BranchResponse> Deactivate(int id, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        if (branch.IsActive)
        {
            var count = await futureAppointments.CountForBranchAsync(id, cancellationToken);
            if (count > 0)
            {
                throw new ConflictException(
                    $"La sucursal tiene {FutureAppointments.Describe(count)}. Cancelalos o esperá a que pasen antes de desactivarla.",
                    ErrorCodes.HasFutureAppointments);
            }

            branch.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return BranchResponse.From(branch);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/activate")]
    public async Task<BranchResponse> Activate(int id, CancellationToken cancellationToken)
    {
        var branch = await FindAsync(id, cancellationToken);
        if (!branch.IsActive)
        {
            branch.IsActive = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return BranchResponse.From(branch);
    }

    private async Task<Branch> FindAsync(int id, CancellationToken cancellationToken) =>
        await db.Branches.FirstOrDefaultAsync(b => b.Id == id, cancellationToken)
        ?? throw new NotFoundException("No se encontró la sucursal.");

    private static void Apply(Branch branch, BranchRequest request)
    {
        branch.Name = request.Name!.Trim();
        branch.Address = request.Address!.Trim();
        branch.Phone = AccountRules.TrimToNull(request.Phone);
        branch.ImageUrl = AccountRules.TrimToNull(request.ImageUrl);
    }
}

[ApiController]
[Route("cuts")]
public sealed class CutsController(AppDbContext db, FutureAppointments futureAppointments) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public async Task<IReadOnlyList<CutResponse>> List([FromQuery] bool includeInactive, CancellationToken cancellationToken)
    {
        User.EnsureAdminWhen(includeInactive);
        var cuts = await db.Cuts.AsNoTracking()
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return cuts.Select(CutResponse.From).ToList();
    }

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<CutResponse> Get(int id, CancellationToken cancellationToken)
    {
        var cut = await db.Cuts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (cut is null || (!cut.IsActive && !User.IsAdmin()))
        {
            throw new NotFoundException("No se encontró el servicio.");
        }

        return CutResponse.From(cut);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<CutResponse>> Create(CutRequest request, CancellationToken cancellationToken)
    {
        var cut = new Cut();
        Apply(cut, request);
        db.Cuts.Add(cut);
        await db.SaveChangesAsync(cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = cut.Id }, CutResponse.From(cut));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<CutResponse> Update(int id, CutRequest request, CancellationToken cancellationToken)
    {
        var cut = await FindAsync(id, cancellationToken);
        Apply(cut, request);
        await db.SaveChangesAsync(cancellationToken);
        return CutResponse.From(cut);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/deactivate")]
    public async Task<CutResponse> Deactivate(int id, CancellationToken cancellationToken)
    {
        var cut = await FindAsync(id, cancellationToken);
        if (cut.IsActive)
        {
            var count = await futureAppointments.CountForCutAsync(id, cancellationToken);
            if (count > 0)
            {
                throw new ConflictException(
                    $"El servicio tiene {FutureAppointments.Describe(count)}. Cancelalos o esperá a que pasen antes de desactivarlo.",
                    ErrorCodes.HasFutureAppointments);
            }

            cut.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return CutResponse.From(cut);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/activate")]
    public async Task<CutResponse> Activate(int id, CancellationToken cancellationToken)
    {
        var cut = await FindAsync(id, cancellationToken);
        if (!cut.IsActive)
        {
            cut.IsActive = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return CutResponse.From(cut);
    }

    private async Task<Cut> FindAsync(int id, CancellationToken cancellationToken) =>
        await db.Cuts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw new NotFoundException("No se encontró el servicio.");

    private static void Apply(Cut cut, CutRequest request)
    {
        cut.Name = request.Name!.Trim();
        cut.Price = request.Price!.Value;
        cut.DurationMinutes = request.DurationMinutes!.Value;
        cut.Description = AccountRules.TrimToNull(request.Description);
    }
}

[ApiController]
[Route("barbers")]
public sealed class BarbersController(
    AppDbContext db,
    IPasswordHasher hasher,
    FutureAppointments futureAppointments,
    Schedule.SlotGenerator slotGenerator,
    Schedule.ScheduleManagement schedule,
    Appointments.AppointmentNotifications notifications)
    : ControllerBase
{
    private const string NotFoundMessage = "No se encontró el barbero.";

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken cancellationToken)
    {
        User.EnsureAdminWhen(includeInactive);
        if (includeInactive)
        {
            var all = await Barbers(db.Users.AsNoTracking())
                .OrderBy(u => u.Name)
                .Select(ToAdmin)
                .ToListAsync(cancellationToken);
            return Ok(all);
        }

        var visible = await Barbers(db.Users.AsNoTracking())
            .Where(u => u.IsActive && u.Branch!.IsActive)
            .OrderBy(u => u.Name)
            .Select(ToPublic)
            .ToListAsync(cancellationToken);
        return Ok(visible);
    }

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var query = Barbers(db.Users.AsNoTracking()).Where(u => u.Id == id);
        if (User.IsAdmin())
        {
            return Ok(await query.Select(ToAdmin).FirstOrDefaultAsync(cancellationToken)
                      ?? throw new NotFoundException(NotFoundMessage));
        }

        return Ok(await query.Where(u => u.IsActive && u.Branch!.IsActive).Select(ToPublic)
                      .FirstOrDefaultAsync(cancellationToken)
                  ?? throw new NotFoundException(NotFoundMessage));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<ActionResult<AdminBarberResponse>> Create(CreateBarberRequest request, CancellationToken cancellationToken)
    {
        await EnsureActiveBranchAsync(request.BranchId!.Value, cancellationToken);

        var email = Domain.Entities.User.NormalizeEmail(request.Email!);
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException(AuthController.EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        var barber = new User
        {
            Name = request.Name!.Trim(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password!),
            Role = UserRole.Barber,
            BranchId = request.BranchId,
            StartDate = request.StartDate,
            ImageUrl = AccountRules.TrimToNull(request.ImageUrl),
            IsActive = true,
            EmailVerified = true,
        };
        db.Users.Add(barber);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException(AuthController.EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        await slotGenerator.GenerateAsync(barber.Id, cancellationToken: cancellationToken);
        var response = await LoadAdminAsync(barber.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = barber.Id }, response);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<AdminBarberResponse> Update(int id, UpdateBarberRequest request, CancellationToken cancellationToken)
    {
        var barber = await FindAsync(id, cancellationToken);
        var branchId = request.BranchId!.Value;

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        IReadOnlyList<int> cancelled = [];
        if (branchId != barber.BranchId)
        {
            await EnsureActiveBranchAsync(branchId, cancellationToken);
            var count = await futureAppointments.CountForBarberAsync(id, cancellationToken);
            if (count > 0 && request.Confirm != true)
            {
                throw new ConflictException(
                    $"El barbero tiene {FutureAppointments.Describe(count)}. Si lo cambiás de sucursal se cancelan todos (con reembolso total de los pagados online). Revisá el resumen y confirmá el cambio.",
                    ErrorCodes.HasFutureAppointments);
            }

            if (count > 0)
            {
                cancelled = await schedule.CancelForBranchChangeAsync(id, User.GetRequiredUserId(), cancellationToken);
            }
        }

        barber.Name = request.Name!.Trim();
        barber.BranchId = branchId;
        barber.StartDate = request.StartDate;
        barber.ImageUrl = AccountRules.TrimToNull(request.ImageUrl);
        barber.Phone = null;
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            barber.PasswordHash = hasher.Hash(request.NewPassword);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await notifications.SendCancellationsAsync(cancelled, null, cancellationToken);
        return await LoadAdminAsync(id, cancellationToken);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("{id}/branch-change-preview")]
    public async Task<Schedule.BranchChangePreview> BranchChangePreview(int id, [FromQuery] int? branchId, CancellationToken cancellationToken)
    {
        if (branchId is null or <= 0)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["branchId"] = ["Elegí la sucursal nueva."] });
        }

        await FindAsync(id, cancellationToken);
        await EnsureActiveBranchAsync(branchId.Value, cancellationToken);
        return await schedule.PreviewBranchChangeAsync(id, branchId.Value, cancellationToken);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/deactivate")]
    public async Task<AdminBarberResponse> Deactivate(int id, CancellationToken cancellationToken)
    {
        var barber = await FindAsync(id, cancellationToken);
        if (barber.IsActive)
        {
            var count = await futureAppointments.CountForBarberAsync(id, cancellationToken);
            if (count > 0)
            {
                throw new ConflictException(
                    $"El barbero tiene {FutureAppointments.Describe(count)}. Cancelalos o esperá a que pasen antes de desactivarlo.",
                    ErrorCodes.HasFutureAppointments);
            }

            barber.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await LoadAdminAsync(id, cancellationToken);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/activate")]
    public async Task<AdminBarberResponse> Activate(int id, CancellationToken cancellationToken)
    {
        var barber = await FindAsync(id, cancellationToken);
        if (!barber.IsActive)
        {
            barber.IsActive = true;
            await db.SaveChangesAsync(cancellationToken);
            await slotGenerator.GenerateAsync(id, cancellationToken: cancellationToken);
        }

        return await LoadAdminAsync(id, cancellationToken);
    }

    private static IQueryable<User> Barbers(IQueryable<User> users) => users.Where(u => u.Role == UserRole.Barber && u.BranchId != null);

    private static readonly Expression<Func<User, PublicBarberResponse>> ToPublic = u =>
        new PublicBarberResponse(u.Id, u.Name, u.ImageUrl, u.BranchId!.Value, new BranchSummary(u.Branch!.Id, u.Branch.Name), u.StartDate);

    private static readonly Expression<Func<User, AdminBarberResponse>> ToAdmin = u =>
        new AdminBarberResponse(u.Id, u.Name, u.Email, u.ImageUrl, u.BranchId!.Value, new BranchSummary(u.Branch!.Id, u.Branch.Name),
            u.StartDate, u.IsActive, u.Branch.IsActive);

    private async Task<User> FindAsync(int id, CancellationToken cancellationToken) =>
        await Barbers(db.Users).FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
        ?? throw new NotFoundException(NotFoundMessage);

    private async Task<AdminBarberResponse> LoadAdminAsync(int id, CancellationToken cancellationToken) =>
        await Barbers(db.Users.AsNoTracking()).Where(u => u.Id == id).Select(ToAdmin).FirstAsync(cancellationToken);

    private async Task EnsureActiveBranchAsync(int branchId, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
        if (branch is null)
        {
            throw new BadRequestException("La sucursal elegida no existe.", ErrorCodes.BranchInactive);
        }

        if (!branch.IsActive)
        {
            throw new BadRequestException("La sucursal elegida está desactivada.", ErrorCodes.BranchInactive);
        }
    }
}
