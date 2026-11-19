using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Payments;

public sealed class MercadoPagoSettings
{
    public const string SectionName = "MercadoPago";

    public string ApiUrl { get; set; } = "https://api.mercadopago.com/";
    public int TimeoutSeconds { get; set; } = 20;
    public string AccessToken { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public string NotificationUrl { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken);
}

public sealed record PreferenceItem(string Id, string Title, decimal UnitPrice);

public sealed record PreferenceRequest(string ExternalReference, PreferenceItem Item, DateTimeOffset ExpiresAt, string? PayerEmail);

public sealed record CreatedPreference(string Id, string CheckoutUrl);

public sealed record GatewayPayment(
    string Id,
    string Status,
    string? ExternalReference,
    decimal Amount,
    DateTimeOffset? ApprovedAt);

public sealed class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface IPaymentGateway
{
    bool IsConfigured { get; }

    Task<CreatedPreference> CreatePreferenceAsync(PreferenceRequest request, CancellationToken cancellationToken);

    Task ExpirePreferenceAsync(string preferenceId, CancellationToken cancellationToken);

    Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken);

    Task RefundAsync(string paymentId, CancellationToken cancellationToken);

    bool IsValidWebhookSignature(string? signatureHeader, string? requestId, string? dataId);
}

public sealed class MercadoPagoClient(HttpClient http, IOptions<MercadoPagoSettings> options, TimeProvider time) : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private MercadoPagoSettings Settings => options.Value;

    public bool IsConfigured => Settings.IsConfigured;

    public async Task<CreatedPreference> CreatePreferenceAsync(PreferenceRequest request, CancellationToken cancellationToken)
    {
        var returnUrl = Settings.ReturnUrl;
        var body = new Dictionary<string, object?>
        {
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = request.Item.Id,
                    ["title"] = request.Item.Title,
                    ["quantity"] = 1,
                    ["unit_price"] = request.Item.UnitPrice,
                    ["currency_id"] = "ARS",
                },
            },
            ["external_reference"] = request.ExternalReference,
            ["expires"] = true,
            ["expiration_date_from"] = Format(time.GetUtcNow().AddMinutes(-1)),
            ["expiration_date_to"] = Format(request.ExpiresAt),
            ["date_of_expiration"] = Format(request.ExpiresAt),
            ["binary_mode"] = true,
            ["statement_descriptor"] = "CRANEO BARBERO",
        };

        if (!string.IsNullOrWhiteSpace(request.PayerEmail))
        {
            body["payer"] = new Dictionary<string, object?> { ["email"] = request.PayerEmail };
        }

        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            body["back_urls"] = new Dictionary<string, object?>
            {
                ["success"] = returnUrl,
                ["failure"] = returnUrl,
                ["pending"] = returnUrl,
            };
            if (returnUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                body["auto_return"] = "approved";
            }
        }

        if (!string.IsNullOrWhiteSpace(Settings.NotificationUrl))
        {
            body["notification_url"] = Settings.NotificationUrl;
        }

        using var message = Request(HttpMethod.Post, "checkout/preferences", body);
        var json = await SendAsync(message, cancellationToken);
        var id = json.GetProperty("id").GetString()
                 ?? throw new PaymentGatewayException("Mercado Pago no devolvió el identificador de la preferencia.");
        var url = json.TryGetProperty("init_point", out var point) ? point.GetString() : null;
        return new CreatedPreference(id, url ?? throw new PaymentGatewayException("Mercado Pago no devolvió el enlace de pago."));
    }

    public async Task ExpirePreferenceAsync(string preferenceId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        using var message = Request(HttpMethod.Put, $"checkout/preferences/{Uri.EscapeDataString(preferenceId)}", new Dictionary<string, object?>
        {
            ["expires"] = true,
            ["expiration_date_from"] = Format(now.AddMinutes(-2)),
            ["expiration_date_to"] = Format(now.AddMinutes(-1)),
        });
        await SendAsync(message, cancellationToken);
    }

    public async Task<GatewayPayment?> GetPaymentAsync(string paymentId, CancellationToken cancellationToken)
    {
        using var message = Request(HttpMethod.Get, $"v1/payments/{Uri.EscapeDataString(paymentId)}", null);
        JsonElement json;
        try
        {
            json = await SendAsync(message, cancellationToken);
        }
        catch (PaymentGatewayException exception) when (exception.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }

        var approved = json.TryGetProperty("date_approved", out var date) && date.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(date.GetString()!, CultureInfo.InvariantCulture).ToUniversalTime()
            : (DateTimeOffset?)null;
        return new GatewayPayment(
            json.GetProperty("id").ToString(),
            json.GetProperty("status").GetString() ?? string.Empty,
            json.TryGetProperty("external_reference", out var reference) && reference.ValueKind == JsonValueKind.String ? reference.GetString() : null,
            json.TryGetProperty("transaction_amount", out var amount) && amount.ValueKind == JsonValueKind.Number ? amount.GetDecimal() : 0m,
            approved);
    }

    public async Task RefundAsync(string paymentId, CancellationToken cancellationToken)
    {
        using var message = Request(HttpMethod.Post, $"v1/payments/{Uri.EscapeDataString(paymentId)}/refunds", new Dictionary<string, object?>());
        message.Headers.Add("X-Idempotency-Key", $"refund-{paymentId}");
        await SendAsync(message, cancellationToken);
    }

    public bool IsValidWebhookSignature(string? signatureHeader, string? requestId, string? dataId)
    {
        if (string.IsNullOrWhiteSpace(Settings.WebhookSecret) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        string? ts = null;
        string? v1 = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length != 2) continue;
            var key = pieces[0].Trim();
            var value = pieces[1].Trim();
            if (key == "ts") ts = value;
            else if (key == "v1") v1 = value;
        }

        if (ts is null || v1 is null)
        {
            return false;
        }

        var manifest = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId)) manifest.Append("id:").Append(dataId.ToLowerInvariant()).Append(';');
        if (!string.IsNullOrEmpty(requestId)) manifest.Append("request-id:").Append(requestId).Append(';');
        manifest.Append("ts:").Append(ts).Append(';');

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Settings.WebhookSecret));
        var expected = Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest.ToString())));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1.ToLowerInvariant()));
    }

    private HttpRequestMessage Request(HttpMethod method, string path, object? body)
    {
        if (!Settings.IsConfigured)
        {
            throw new PaymentGatewayException("Mercado Pago no está configurado (falta MP_ACCESS_TOKEN).");
        }

        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.AccessToken);
        if (body is not null)
        {
            message.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        }

        return message;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new PaymentGatewayException("No se pudo conectar con Mercado Pago.", exception);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new PaymentGatewayException($"Mercado Pago respondió {(int)response.StatusCode}.");
            }

            return string.IsNullOrWhiteSpace(content)
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : JsonDocument.Parse(content).RootElement.Clone();
        }
    }

    private static string Format(DateTimeOffset instant) =>
        instant.ToOffset(TimeSpan.FromHours(-3)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
}
