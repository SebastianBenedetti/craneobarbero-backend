using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Domain.Entities;
using CraneoBarbero.Api.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("images")]
public sealed class ImagesController(AppDbContext db, IConfiguration configuration, TimeProvider time) : ControllerBase
{
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            throw new BadRequestException("Elegí una imagen para subir.", ErrorCodes.InvalidImage);
        }

        if (file.Length > ImageFiles.MaxBytes)
        {
            throw new BadRequestException("La imagen es demasiado grande. Probá con una más chica.", ErrorCodes.InvalidImage);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        var contentType = ImageFiles.DetectContentType(data)
            ?? throw new BadRequestException("La imagen tiene que ser JPG, PNG o WebP.", ErrorCodes.InvalidImage);

        var userId = User.GetRequiredUserId();
        if (User.GetRole() != Roles.Admin)
        {
            var since = time.GetUtcNow().AddDays(-1);
            var recent = await db.Images.CountAsync(i => i.CreatedById == userId && i.CreatedAt >= since, cancellationToken);
            if (recent >= ImageFiles.DailyLimitPerUser)
            {
                throw new TooManyRequestsException("Subiste demasiadas imágenes hoy. Probá de nuevo mañana.");
            }
        }

        var image = new StoredImage { ContentType = contentType, Data = data, CreatedById = userId };
        db.Images.Add(image);
        await db.SaveChangesAsync(cancellationToken);

        var baseUrl = configuration[EnvironmentVariables.RenderExternalUrl]?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        }

        return StatusCode(StatusCodes.Status201Created, new ImageUploadResponse(image.Id, $"{baseUrl}/images/{image.Id}"));
    }

    [AllowAnonymous]
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id, CancellationToken cancellationToken)
    {
        var image = await db.Images.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { i.ContentType, i.Data })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException();

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
        Response.Headers.AccessControlAllowOrigin = "*";
        return File(image.Data, image.ContentType);
    }
}
