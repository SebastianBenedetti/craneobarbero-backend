namespace CraneoBarbero.Api.Configuration;

public sealed class EmailSettings
{
    public const string SectionName = "Email";

    public string BrevoApiKey { get; set; } = string.Empty;
    public string BrevoApiUrl { get; set; } = "https://api.brevo.com/v3/";
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Cráneo Barbero";
    public string FrontendBaseUrl { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
    public int RetryDelaySeconds { get; set; } = 5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BrevoApiKey) && !string.IsNullOrWhiteSpace(FromEmail);
}
