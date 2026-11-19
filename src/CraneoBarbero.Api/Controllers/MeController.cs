using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Contracts;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

public sealed record UpdateProfileRequest(string? Name, string? Phone, string? ImageUrl);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.ImageUrl).OptionalImageUrl();
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty().WithMessage("Ingresá tu contraseña actual.")
            .MaximumLength(128).WithMessage("La contraseña es demasiado larga.");
        RuleFor(r => r.NewPassword).StrongPassword();
    }
}

[ApiController]
[Route("me")]
public sealed class MeController(AppDbContext db, IPasswordHasher hasher) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<UserResponse>> Get(CancellationToken cancellationToken)
    {
        var user = await FindCurrentUserAsync(tracking: false, cancellationToken);
        return Ok(UserResponse.From(user));
    }

    [HttpPut]
    public async Task<ActionResult<UserResponse>> Update(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        var user = await FindCurrentUserAsync(tracking: true, cancellationToken);

        user.Name = request.Name!.Trim();
        user.Phone = user.Role == Domain.Enums.UserRole.Barber ? null : AccountRules.TrimToNull(request.Phone);
        user.ImageUrl = AccountRules.TrimToNull(request.ImageUrl);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(UserResponse.From(user));
    }

    [HttpPut("password")]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<MessageResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await FindCurrentUserAsync(tracking: true, cancellationToken);

        if (!hasher.Verify(request.CurrentPassword!, user.PasswordHash))
        {
            throw new BadRequestException("La contraseña actual no es correcta.", ErrorCodes.InvalidCurrentPassword);
        }

        if (hasher.Verify(request.NewPassword!, user.PasswordHash))
        {
            throw new BadRequestException("La nueva contraseña tiene que ser distinta de la actual.", ErrorCodes.SamePassword);
        }

        user.PasswordHash = hasher.Hash(request.NewPassword!);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new MessageResponse("Tu contraseña se cambió."));
    }

    private async Task<Domain.Entities.User> FindCurrentUserAsync(bool tracking, CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var query = tracking ? db.Users : db.Users.AsNoTracking();
        return await query.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
               ?? throw new UnauthorizedException(ErrorMessages.Unauthorized);
    }
}
