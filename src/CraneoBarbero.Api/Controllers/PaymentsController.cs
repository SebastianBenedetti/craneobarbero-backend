using System.Text.Json;
using CraneoBarbero.Api.Appointments;
using CraneoBarbero.Api.Auth;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Data;
using CraneoBarbero.Api.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CraneoBarbero.Api.Controllers;

[ApiController]
[Route("payments")]
public sealed class PaymentsController(
    AppDbContext db,
    AppointmentPayments payments,
    IPaymentGateway gateway,
    ILogger<PaymentsController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken cancellationToken)
    {
        var type = Request.Query["type"].FirstOrDefault() ?? Request.Query["topic"].FirstOrDefault();
        var dataId = Request.Query["data.id"].FirstOrDefault() ?? Request.Query["id"].FirstOrDefault();

        if (Request.ContentLength is > 0)
        {
            try
            {
                using var body = await JsonDocument.ParseAsync(Request.Body, cancellationToken: cancellationToken);
                var root = body.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (type is null && root.TryGetProperty("type", out var bodyType)) type = bodyType.GetString();
                    if (dataId is null && root.TryGetProperty("data", out var data) && data.TryGetProperty("id", out var id))
                    {
                        dataId = id.ToString();
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (!string.Equals(type, "payment", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(dataId))
        {
            return Ok();
        }

        if (!gateway.IsValidWebhookSignature(Request.Headers["x-signature"].FirstOrDefault(), Request.Headers["x-request-id"].FirstOrDefault(), dataId))
        {
            var legacyNotice = Request.Query.ContainsKey("topic") && !Request.Query.ContainsKey("data.id");
            if (legacyNotice)
            {
                logger.LogInformation("Aviso de Mercado Pago en formato IPN sin firma: se ignora");
                return Ok();
            }

            logger.LogWarning("Notificación de Mercado Pago con firma inválida");
            throw new UnauthorizedException("Firma inválida.", ErrorCodes.InvalidSignature);
        }

        await payments.ProcessGatewayPaymentAsync(dataId, cancellationToken);
        return Ok();
    }

    [Authorize(Policy = Policies.Client)]
    [HttpGet("return")]
    public async Task<PaymentReturnResponse> Return(
        [FromQuery(Name = "payment_id")] string? paymentId,
        [FromQuery(Name = "collection_id")] string? collectionId,
        [FromQuery(Name = "external_reference")] string? externalReference,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var clientId = User.GetRequiredUserId();
        var gatewayId = new[] { paymentId, collectionId }.FirstOrDefault(id => !string.IsNullOrWhiteSpace(id) && id != "null");
        int? appointmentId = AppointmentPayments.TryParseReference(externalReference, out var parsed) ? parsed : null;

        if (gatewayId is not null && gatewayId.All(char.IsAsciiLetterOrDigit) && gatewayId.Length <= 40)
        {
            appointmentId = await payments.ProcessGatewayPaymentAsync(gatewayId, cancellationToken) ?? appointmentId;
        }

        if (appointmentId is null)
        {
            throw new BadRequestException("No se pudo identificar el turno del pago.", ErrorCodes.BadRequest);
        }

        var appointment = await db.Appointments.AsNoTracking()
                              .Include(a => a.Client).Include(a => a.Barber).Include(a => a.Cut).Include(a => a.Branch)
                              .FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken)
                          ?? throw new NotFoundException("No se encontró el turno.");
        if (appointment.ClientId != clientId)
        {
            throw new ForbiddenException();
        }

        return new PaymentReturnResponse(status, AppointmentResponse.From(appointment));
    }
}
