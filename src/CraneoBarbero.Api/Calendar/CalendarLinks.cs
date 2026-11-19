using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Calendar;

public sealed class CalendarLinks(IOptions<JwtSettings> jwt, IOptions<EmailSettings> email, BusinessClock clock)
{
    public static readonly TimeSpan ValidAfterAppointment = TimeSpan.FromDays(1);

    private readonly byte[] _key = HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(jwt.Value.Secret), Encoding.UTF8.GetBytes("calendar-links"));

    public string CreateToken(int appointmentId, DateTimeOffset expiresAt)
    {
        var payload = $"{appointmentId.ToString(CultureInfo.InvariantCulture)}.{expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        return $"{payload}.{Sign(payload)}";
    }

    public string? CreateUrl(int appointmentId, DateTimeOffset appointmentEnd)
    {
        var baseUrl = email.Value.ApiBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return $"{baseUrl.TrimEnd('/')}/calendar/{CreateToken(appointmentId, appointmentEnd + ValidAfterAppointment)}.ics";
    }

    public bool TryRead(string? token, out int appointmentId)
    {
        appointmentId = 0;
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign($"{parts[0]}.{parts[1]}"));
        var actual = Encoding.ASCII.GetBytes(parts[2]);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual) ||
            DateTimeOffset.FromUnixTimeSeconds(expires) < clock.UtcNow)
        {
            return false;
        }

        appointmentId = id;
        return true;
    }

    private string Sign(string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
}
