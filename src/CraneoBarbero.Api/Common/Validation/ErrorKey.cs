namespace CraneoBarbero.Api.Common.Validation;

public static class ErrorKey
{
    public static string ToCamelCase(string key)
    {
        if (string.IsNullOrEmpty(key) || key == "$")
        {
            return "body";
        }

        var trimmed = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('.', parts.Select(part => part.Length == 0 ? part : char.ToLowerInvariant(part[0]) + part[1..]));
    }
}
