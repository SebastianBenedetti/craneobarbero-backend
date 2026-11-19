namespace CraneoBarbero.Api.Configuration;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public const int MinimumSecretBytes = 32;

    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "craneobarbero-api";
    public string Audience { get; set; } = "craneobarbero-frontend";
    public int ExpirationMinutes { get; set; } = 1440;
}
