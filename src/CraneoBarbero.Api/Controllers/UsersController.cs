using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Email;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

public sealed record UserListQuery(int? Page, int? PageSize, UserRole? Role, bool? Active, string? Q);

public sealed record CreateUserRequest(string? Name, string? Email, string? Password, string? Phone, UserRole? Role);

public sealed record UpdateUserRequest(string? Name, string? Phone, string? ImageUrl, string? NewPassword);

public sealed record AdminUserResponse(
    int Id,
    string Name,
    string Email,
    string? Phone,
    string? ImageUrl,
    UserRole Role,
    int? BranchId,
    bool IsActive,
    bool EmailVerified,
    DateTimeOffset CreatedAt)
{
    public static AdminUserResponse From(User user) => new(
        user.Id, user.Name, user.Email, user.Phone, user.ImageUrl, user.Role, user.BranchId, user.IsActive, user.EmailVerified, user.CreatedAt);
}

public sealed record UserClosureResponse(AdminUserResponse? User, int CancelledAppointments, int RefundedAppointments, decimal TotalRefunded)
{
    public static UserClosureResponse From(AdminUserResponse? user, ClosureResult result) =>
        new(user, result.CancelledAppointments, result.RefundedAppointments, result.TotalRefunded);
}

public sealed class UserListQueryValidator : AbstractValidator<UserListQuery>
{
    public UserListQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0).When(q => q.Page is not null).WithMessage("La página debe ser mayor a 0.");
        RuleFor(q => q.PageSize).InclusiveBetween(1, AppointmentLimits.MaxPageSize).When(q => q.PageSize is not null)
            .WithMessage($"El tamaño de página debe estar entre 1 y {AppointmentLimits.MaxPageSize}.");
        RuleFor(q => q.Q).MaximumLength(100).WithMessage("La búsqueda es demasiado larga.");
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.Email).EmailAddress();
        RuleFor(r => r.Password).StrongPassword();
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.Role).NotNull().WithMessage("Elegí el rol.")
            .Must(role => role is null or UserRole.Client or UserRole.Admin)
            .WithMessage("Desde acá solo se crean clientes y administradores. Los barberos se crean en la sección Barberos.");
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
        RuleFor(r => r.NewPassword).StrongPassword().When(r => !string.IsNullOrEmpty(r.NewPassword));
    }
}

[ApiController]
[Route("users")]
[Authorize(Policy = Policies.Admin)]
public sealed class UsersController(
    AppDbContext db,
    IPasswordHasher hasher,
    AccountClosure closure,
    AuthTokenService authTokens,
    AccountEmailService emails) : ControllerBase
{
    private const string NotFoundMessage = "No se encontró el usuario.";
    private const string BarbersMessage = "Los barberos se gestionan desde la sección Barberos.";

    [HttpGet]
    public async Task<PagedResponse<AdminUserResponse>> List([FromQuery] UserListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page ?? 1;
        var pageSize = query.PageSize ?? AppointmentLimits.DefaultPageSize;
        var users = db.Users.AsNoTracking().AsQueryable();
        if (query.Role is { } role)
        {
            users = users.Where(u => u.Role == role);
        }

        if (query.Active is { } active)
        {
            users = users.Where(u => u.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var term = query.Q.Trim().ToLowerInvariant();
            users = db.Database.IsNpgsql()
                ? users.Where(u => EF.Functions.ILike(u.Name, $"%{EscapeLike(term)}%", "\\") ||
                                   EF.Functions.ILike(u.Email, $"%{EscapeLike(term)}%", "\\"))
                : users.Where(u => u.Name.ToLower().Contains(term) || u.Email.Contains(term));
        }

        var total = await users.CountAsync(cancellationToken);
        var items = await users
            .OrderBy(u => u.Name).ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResponse<AdminUserResponse>(items.Select(AdminUserResponse.From).ToList(), page, pageSize, total);
    }

    [HttpGet("{id}")]
    public async Task<AdminUserResponse> Get(int id, CancellationToken cancellationToken) =>
        AdminUserResponse.From(await FindAsync(id, tracking: false, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<AdminUserResponse>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var email = Domain.Entities.User.NormalizeEmail(request.Email!);
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException(AuthController.EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        var isAdmin = request.Role == UserRole.Admin;
        var user = new User
        {
            Name = request.Name!.Trim(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password!),
            Phone = AccountRules.TrimToNull(request.Phone),
            Role = request.Role!.Value,
            IsActive = true,
            EmailVerified = isAdmin,
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException(AuthController.EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        if (!isAdmin)
        {
            var token = await authTokens.IssueAsync(user.Id, AuthTokenType.EmailVerification, cancellationToken);
            await emails.SendVerificationAsync(user, token, cancellationToken);
        }

        return CreatedAtAction(nameof(Get), new { id = user.Id }, AdminUserResponse.From(user));
    }

    [HttpPut("{id}")]
    public async Task<AdminUserResponse> Update(int id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: true, cancellationToken);
        EnsureNotBarber(user);

        user.Name = request.Name!.Trim();
        user.Phone = AccountRules.TrimToNull(request.Phone);
        user.ImageUrl = AccountRules.TrimToNull(request.ImageUrl);
        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            user.PasswordHash = hasher.Hash(request.NewPassword);
        }

        await db.SaveChangesAsync(cancellationToken);
        return AdminUserResponse.From(user);
    }

    [HttpGet("{id}/closure-preview")]
    public async Task<ClosurePreview> ClosurePreview(int id, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: false, cancellationToken);
        if (user.Role != UserRole.Client)
        {
            throw new BadRequestException("Esta acción solo se aplica a cuentas de clientes.", ErrorCodes.NotAClient);
        }

        return await closure.PreviewAsync(id, CurrentActor(), cancellationToken);
    }

    [HttpPost("{id}/deactivate")]
    public async Task<UserClosureResponse> Deactivate(int id, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: true, cancellationToken);
        EnsureNotBarber(user);
        if (!user.IsActive)
        {
            return UserClosureResponse.From(AdminUserResponse.From(user), new ClosureResult(0, 0, 0m));
        }

        if (user.Role == UserRole.Admin)
        {
            await EnsureAnotherActiveAdminAsync(user.Id, "desactivar", cancellationToken);
            user.IsActive = false;
            await db.SaveChangesAsync(cancellationToken);
            return UserClosureResponse.From(AdminUserResponse.From(user), new ClosureResult(0, 0, 0m));
        }

        var result = await closure.DeactivateAsync(user, CurrentActor(), cancellationToken);
        return UserClosureResponse.From(AdminUserResponse.From(user), result);
    }

    [HttpPost("{id}/activate")]
    public async Task<AdminUserResponse> Activate(int id, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: true, cancellationToken);
        EnsureNotBarber(user);
        if (!user.IsActive)
        {
            user.IsActive = true;
            await db.SaveChangesAsync(cancellationToken);
        }

        return AdminUserResponse.From(user);
    }

    [HttpDelete("{id}")]
    public async Task<UserClosureResponse> Delete(int id, CancellationToken cancellationToken)
    {
        var user = await FindAsync(id, tracking: true, cancellationToken);
        if (user.Role != UserRole.Client)
        {
            throw new BadRequestException(
                "Solo se pueden eliminar cuentas de clientes. Los barberos y administradores se desactivan.", ErrorCodes.NotAClient);
        }

        var result = await closure.DeleteAsync(user, CurrentActor(), cancellationToken);
        return UserClosureResponse.From(null, result);
    }

    private Actor CurrentActor() => new(User.GetRequiredUserId(), UserRole.Admin);

    private async Task EnsureAnotherActiveAdminAsync(int adminId, string action, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != adminId, cancellationToken))
        {
            throw new ConflictException(
                $"Tiene que quedar al menos un administrador activo: no se puede {action} al último.", ErrorCodes.LastAdmin);
        }
    }

    private static void EnsureNotBarber(User user)
    {
        if (user.Role == UserRole.Barber)
        {
            throw new BadRequestException(BarbersMessage, ErrorCodes.UseBarbersEndpoint);
        }
    }

    private async Task<User> FindAsync(int id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Users : db.Users.AsNoTracking();
        return await query.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
               ?? throw new NotFoundException(NotFoundMessage);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
