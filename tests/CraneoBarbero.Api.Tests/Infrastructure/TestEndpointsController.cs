using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Domain.Enums;
using CraneoBarbero.Api.Security;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CraneoBarbero.Api.Tests.Infrastructure;

public sealed record EchoRequest(string? Email, string? Name);

public sealed class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().EmailAddress().WithMessage("El email no es válido.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(10).WithMessage("El nombre es obligatorio.");
    }
}

[ApiController]
[Route("test")]
public sealed class TestEndpointsController(IAuthorizationService authorization) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("public")]
    public IActionResult Public() => Ok(new { ok = true });

    [HttpGet("me")]
    public IActionResult Me() => Ok(new { userId = User.GetRequiredUserId(), role = User.GetRole() });

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("admin")]
    public IActionResult Admin() => Ok();

    [Authorize(Policy = Policies.Client)]
    [HttpGet("client")]
    public IActionResult Client() => Ok();

    [Authorize(Policy = Policies.BarberOrAdmin)]
    [HttpGet("barber-or-admin")]
    public IActionResult BarberOrAdmin() => Ok();

    [HttpGet("owned/{ownerId}")]
    public async Task<IActionResult> Owned(int ownerId)
    {
        await authorization.EnsureAuthorizedAsync(User, new OwnedResource(ownerId));
        return Ok();
    }

    [HttpGet("owned-only/{ownerId}")]
    public async Task<IActionResult> OwnedOnly(int ownerId)
    {
        await authorization.EnsureAuthorizedAsync(User, new OwnedResource(ownerId), Policies.OwnerOnly);
        return Ok();
    }

    [AllowAnonymous]
    [HttpGet("items/{id}")]
    public IActionResult Item(int id) => Ok(new { id });

    [AllowAnonymous]
    [HttpPost("echo")]
    public IActionResult Echo(EchoRequest request) => Ok(request);

    [AllowAnonymous]
    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("detalle interno Npgsql 42P01");

    [AllowAnonymous]
    [HttpGet("missing")]
    public IActionResult Missing() => throw new NotFoundException();

    [AllowAnonymous]
    [HttpGet("conflict")]
    public IActionResult ThrowConflict() => throw new ConflictException("El horario ya está ocupado.", "slot_taken");

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    [HttpPost("auth-limited")]
    public IActionResult AuthLimited() => Ok();

    [AllowAnonymous]
    [HttpGet("entity")]
    public IActionResult Entity() => Ok(new User
    {
        Id = 1,
        Name = "Persona",
        Email = "persona@example.com",
        PasswordHash = "$2b$12$hashquenodebesalir",
        Role = UserRole.Barber,
    });

    [AllowAnonymous]
    [HttpGet("payment-methods")]
    public IActionResult PaymentMethods() => Ok(Enum.GetValues<PaymentMethod>());
}
