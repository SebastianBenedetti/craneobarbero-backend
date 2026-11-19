using System.Text;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Configuration;

public sealed class JwtSettingsValidator : IValidateOptions<JwtSettings>
{
    public ValidateOptionsResult Validate(string? name, JwtSettings settings)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(settings.Secret))
        {
            errors.Add($"Falta la variable {EnvironmentVariables.JwtSecret}.");
        }
        else if (Encoding.UTF8.GetByteCount(settings.Secret) < JwtSettings.MinimumSecretBytes)
        {
            errors.Add($"{EnvironmentVariables.JwtSecret} debe tener al menos {JwtSettings.MinimumSecretBytes} bytes.");
        }

        if (string.IsNullOrWhiteSpace(settings.Issuer)) errors.Add("Jwt:Issuer es obligatorio.");
        if (string.IsNullOrWhiteSpace(settings.Audience)) errors.Add("Jwt:Audience es obligatorio.");
        if (settings.ExpirationMinutes <= 0) errors.Add("Jwt:ExpirationMinutes debe ser positivo.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class SecuritySettingsValidator : IValidateOptions<SecuritySettings>
{
    public ValidateOptionsResult Validate(string? name, SecuritySettings settings)
    {
        var errors = new List<string>();

        if (settings.MaxRequestBodyBytes <= 0) errors.Add("Security:MaxRequestBodyBytes debe ser positivo.");
        Check("GlobalRateLimit", settings.GlobalRateLimit, errors);
        Check("AuthRateLimit", settings.AuthRateLimit, errors);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(string name, RateLimitSettings limit, List<string> errors)
    {
        if (limit.PermitLimit <= 0 || limit.WindowSeconds <= 0)
        {
            errors.Add($"Security:{name} debe tener PermitLimit y WindowSeconds positivos.");
        }
    }
}

public sealed class DatabaseSettings
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;
}
