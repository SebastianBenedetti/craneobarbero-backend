namespace CraneoBarbero.Api.Configuration;

public sealed class CorsSettings
{
    public const string PolicyName = "Frontend";

    public static string[] ParseOrigins(string? value) =>
        (value ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => origin.TrimEnd('/'))
            .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                             uri.AbsolutePath == "/")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
