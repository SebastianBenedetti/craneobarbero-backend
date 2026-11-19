namespace CraneoBarbero.Api.Configuration;

public sealed class SecuritySettings
{
    public const string SectionName = "Security";

    public long MaxRequestBodyBytes { get; set; } = 1_048_576;
    public RateLimitSettings GlobalRateLimit { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };
    public RateLimitSettings AuthRateLimit { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
}

public sealed class RateLimitSettings
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
}
