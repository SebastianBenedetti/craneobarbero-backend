using Npgsql;

namespace CraneoBarbero.Api.Configuration;

public static class DatabaseConnection
{
    public static string BuildConnectionString(string? databaseUrl, string? rootCertificatePath)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            throw new InvalidOperationException($"Falta la variable {EnvironmentVariables.DatabaseUrl}.");
        }

        var builder = IsUri(databaseUrl) ? FromUri(databaseUrl) : new NpgsqlConnectionStringBuilder(databaseUrl);

        builder.SslMode = SslMode.VerifyFull;
        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        if (!string.IsNullOrWhiteSpace(rootCertificatePath))
        {
            builder.RootCertificate = rootCertificatePath;
        }

        return builder.ConnectionString;
    }

    private static bool IsUri(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

    private static NpgsqlConnectionStringBuilder FromUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException($"La variable {EnvironmentVariables.DatabaseUrl} no tiene un formato válido.");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = string.IsNullOrEmpty(database) ? "postgres" : database,
            Username = Uri.UnescapeDataString(userInfo[0]),
        };

        if (userInfo.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        return builder;
    }
}
