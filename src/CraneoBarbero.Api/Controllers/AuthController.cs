using CraneoBarbero.Api.Accounts;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Contracts;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Email;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RegisterRequest(string? Name, string? Email, string? Password, string? Phone, bool? AcceptTerms);

public sealed record VerifyEmailRequest(string? Token);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? Password);

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

public sealed class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(r => r.Name).PersonName();
        RuleFor(r => r.Email).EmailAddress();
        RuleFor(r => r.Password).StrongPassword();
        RuleFor(r => r.Phone).OptionalPhone();
        RuleFor(r => r.AcceptTerms).Equal(true)
            .WithMessage("Para crear la cuenta tenés que aceptar los términos y condiciones y la política de privacidad.");
    }
}

public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(r => r.Token).NotEmpty().WithMessage("Falta el código del enlace.");
    }
}

public sealed class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(r => r.Email).EmailAddress();
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(r => r.Token).NotEmpty().WithMessage("Falta el código del enlace.");
        RuleFor(r => r.Password).StrongPassword();
    }
}

[ApiController]
[Route("auth")]
[EnableRateLimiting(RateLimitPolicies.Auth)]
public sealed class AuthController(
    AppDbContext db,
    IPasswordHasher hasher,
    IJwtTokenService tokens,
    AuthTokenService authTokens,
    AccountEmailService emails,
    TimeProvider timeProvider) : ControllerBase
{
    public const string InvalidCredentialsMessage = "Email o contraseña incorrectos.";
    public const string InactiveAccountMessage = "Tu cuenta está desactivada, comunicate con el local.";
    public const string EmailInUseMessage = "Ya existe una cuenta con ese email.";
    public const string InvalidTokenMessage = "El enlace no es válido o ya venció. Pedí uno nuevo.";
    public const string ForgotPasswordMessage =
        "Si el email está registrado, te enviamos un enlace para restablecer la contraseña. Revisá tu bandeja de entrada.";
    public const string ResendCooldownMessage =
        "Ya te enviamos un mail hace menos de un minuto. Revisá tu bandeja de entrada o esperá un momento.";

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
            throw new UnauthorizedException(InvalidCredentialsMessage, ErrorCodes.InvalidCredentials);
        }

        if (!user.IsActive)
        {
            throw new ForbiddenException(InactiveAccountMessage, ErrorCodes.AccountInactive);
        }

        var token = tokens.CreateToken(user);
        return Ok(new LoginResponse(token.Token, token.ExpiresAt, UserResponse.From(user)));
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<ActionResult<LoginResponse>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var email = Domain.Entities.User.NormalizeEmail(request.Email!);
        if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException(EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        var user = new User
        {
            Name = request.Name!.Trim(),
            Email = email,
            PasswordHash = hasher.Hash(request.Password!),
            Phone = AccountRules.TrimToNull(request.Phone),
            Role = UserRole.Client,
            IsActive = true,
            EmailVerified = false,
            TermsAcceptedAt = timeProvider.GetUtcNow(),
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException(EmailInUseMessage, ErrorCodes.EmailInUse);
        }

        var verificationToken = await authTokens.IssueAsync(user.Id, AuthTokenType.EmailVerification, cancellationToken);
        await emails.SendVerificationAsync(user, verificationToken, cancellationToken);

        var token = tokens.CreateToken(user);
        return StatusCode(StatusCodes.Status201Created,
            new LoginResponse(token.Token, token.ExpiresAt, UserResponse.From(user)));
    }

    [AllowAnonymous]
    [HttpPost("verify-email")]
    public async Task<ActionResult<MessageResponse>> VerifyEmail(VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        var authToken = await authTokens.ConsumeAsync(request.Token, AuthTokenType.EmailVerification, cancellationToken)
                        ?? throw new BadRequestException(InvalidTokenMessage, ErrorCodes.InvalidToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == authToken.UserId, cancellationToken)
                   ?? throw new BadRequestException(InvalidTokenMessage, ErrorCodes.InvalidToken);
        user.EmailVerified = true;

        await SaveConsumingTokenAsync(cancellationToken);
        return Ok(new MessageResponse("Tu email quedó verificado. Ya podés reservar turnos."));
    }

    [HttpPost("resend-verification")]
    public async Task<ActionResult<MessageResponse>> ResendVerification(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new UnauthorizedException(ErrorMessages.Unauthorized);

        if (user.EmailVerified)
        {
            throw new ConflictException("Tu email ya está verificado.", ErrorCodes.EmailAlreadyVerified);
        }

        if (await authTokens.WasIssuedRecentlyAsync(user.Id, AuthTokenType.EmailVerification, cancellationToken))
        {
            throw new TooManyRequestsException(ResendCooldownMessage);
        }

        var verificationToken = await authTokens.IssueAsync(user.Id, AuthTokenType.EmailVerification, cancellationToken);
        await emails.SendVerificationAsync(user, verificationToken, cancellationToken);

        return Ok(new MessageResponse("Te enviamos un nuevo mail de verificación. Revisá tu bandeja de entrada."));
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<MessageResponse>> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = Domain.Entities.User.NormalizeEmail(request.Email!);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email && u.IsActive, cancellationToken);

        if (user is not null &&
            !await authTokens.WasIssuedRecentlyAsync(user.Id, AuthTokenType.PasswordReset, cancellationToken))
        {
            var resetToken = await authTokens.IssueAsync(user.Id, AuthTokenType.PasswordReset, cancellationToken);
            await emails.SendPasswordResetAsync(user, resetToken, cancellationToken);
        }

        return Ok(new MessageResponse(ForgotPasswordMessage));
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    public async Task<ActionResult<MessageResponse>> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var authToken = await authTokens.ConsumeAsync(request.Token, AuthTokenType.PasswordReset, cancellationToken)
                        ?? throw new BadRequestException(InvalidTokenMessage, ErrorCodes.InvalidToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == authToken.UserId, cancellationToken)
                   ?? throw new BadRequestException(InvalidTokenMessage, ErrorCodes.InvalidToken);

        if (!user.IsActive)
        {
            throw new ForbiddenException(InactiveAccountMessage, ErrorCodes.AccountInactive);
        }

        user.PasswordHash = hasher.Hash(request.Password!);
        user.EmailVerified = true;
        await authTokens.DiscardUnusedAsync(user.Id, AuthTokenType.PasswordReset, cancellationToken);

        await SaveConsumingTokenAsync(cancellationToken);
        return Ok(new MessageResponse("Tu contraseña se cambió. Ya podés iniciar sesión."));
    }

    private async Task SaveConsumingTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BadRequestException(InvalidTokenMessage, ErrorCodes.InvalidToken);
        }
    }
}
