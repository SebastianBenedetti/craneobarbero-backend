using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Contracts;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

public sealed record LoginRequest(string? Email, string? Password);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().WithMessage("Ingresá tu email.")
            .MaximumLength(254).WithMessage("El email es demasiado largo.");
        RuleFor(r => r.Password).NotEmpty().WithMessage("Ingresá tu contraseña.")
            .MaximumLength(128).WithMessage("La contraseña es demasiado larga.");
    }
}

[ApiController]
[Route("auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(AppDbContext db, IPasswordHasher hasher, IJwtTokenService tokens) : ControllerBase
{
    public const string InvalidCredentialsMessage = "Email o contraseña incorrectos.";
    public const string InactiveAccountMessage = "Tu cuenta está desactivada, comunicate con el local.";

    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), 12));

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var email = Domain.Entities.User.NormalizeEmail(request.Email!);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        var passwordMatches = hasher.Verify(request.Password!, user?.PasswordHash ?? DummyHash.Value);
        if (user is null || !passwordMatches)
        {
            throw new UnauthorizedException(InvalidCredentialsMessage, "invalid_credentials");
        }

        if (!user.IsActive)
        {
            throw new ForbiddenException(InactiveAccountMessage, ErrorCodes.AccountInactive);
        }

        var token = tokens.CreateToken(user);
        return Ok(new LoginResponse(token.Token, token.ExpiresAt, UserResponse.From(user)));
    }
}
