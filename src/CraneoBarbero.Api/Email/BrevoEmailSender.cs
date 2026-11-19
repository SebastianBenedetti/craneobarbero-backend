using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Email;

public sealed class BrevoEmailSender(HttpClient http, IOptions<EmailSettings> settings, ILogger<BrevoEmailSender> logger)
    : IEmailSender
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (!options.IsConfigured)
        {
            logger.LogWarning("No se envió el mail \"{Subject}\": faltan {ApiKey} o {From}.",
                message.Subject, EnvironmentVariables.BrevoApiKey, EnvironmentVariables.EmailFrom);
            return false;
        }

        var sender = new BrevoContact(options.FromEmail, options.FromName);
        var body = new BrevoEmailRequest(
            sender,
            [new BrevoContact(message.ToEmail, message.ToName)],
            sender,
            message.Subject,
            message.HtmlBody,
            message.TextBody,
            message.Attachments is { Count: > 0 } attachments
                ? attachments.Select(a => new BrevoAttachment(a.FileName, Convert.ToBase64String(a.Content))).ToList()
                : null);

        using var request = new HttpRequestMessage(HttpMethod.Post, "smtp/email");
        request.Headers.Add("api-key", options.BrevoApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(body, options: SerializerOptions);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        logger.LogWarning("Brevo rechazó el mail \"{Subject}\" con el estado {StatusCode}.",
            message.Subject, (int)response.StatusCode);
        return false;
    }

    private sealed record BrevoContact(string Email, string Name);

    private sealed record BrevoEmailRequest(
        BrevoContact Sender,
        IReadOnlyList<BrevoContact> To,
        BrevoContact ReplyTo,
        string Subject,
        string HtmlContent,
        string TextContent,
        IReadOnlyList<BrevoAttachment>? Attachment);

    private sealed record BrevoAttachment(string Name, string Content);
}
