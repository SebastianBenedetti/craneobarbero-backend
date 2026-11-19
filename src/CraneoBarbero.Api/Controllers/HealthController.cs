using CraneoBarbero.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[AllowAnonymous]
[DisableRateLimiting]
[Route("health")]
public sealed class HealthController(AppDbContext db, ILogger<HealthController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            if (db.Database.IsRelational())
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            }
            else if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                throw new InvalidOperationException("Sin conexión.");
            }
            return Ok(new { status = "ok" });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "La base de datos no responde.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }
}
