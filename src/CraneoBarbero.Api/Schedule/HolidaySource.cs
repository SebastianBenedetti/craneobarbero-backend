using System.Globalization;
using System.Text.Json.Serialization;

namespace CraneoBarbero.Api.Schedule;

public sealed record ExternalHoliday(DateOnly Date, string Name);

public interface IHolidaySource
{
    Task<IReadOnlyList<ExternalHoliday>> GetNationalHolidaysAsync(int year, CancellationToken cancellationToken);
}

public sealed class ArgentinaDatosHolidaySource(HttpClient http) : IHolidaySource
{
    private static readonly HashSet<string> NonWorkingTypes = ["inamovible", "trasladable"];

    public async Task<IReadOnlyList<ExternalHoliday>> GetNationalHolidaysAsync(int year, CancellationToken cancellationToken)
    {
        var items = await http.GetFromJsonAsync<List<Item>>(year.ToString(CultureInfo.InvariantCulture), cancellationToken)
                    ?? [];

        return items
            .Where(item => item.Type is not null && NonWorkingTypes.Contains(item.Type.Trim().ToLowerInvariant()))
            .Where(item => DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(item => new ExternalHoliday(
                DateOnly.ParseExact(item.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Truncate(string.IsNullOrWhiteSpace(item.Name) ? "Feriado nacional" : item.Name.Trim(), 150)))
            .Where(holiday => holiday.Date.Year == year)
            .GroupBy(holiday => holiday.Date)
            .Select(group => group.First())
            .OrderBy(holiday => holiday.Date)
            .ToList();
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private sealed record Item(
        [property: JsonPropertyName("fecha")] string? Date,
        [property: JsonPropertyName("tipo")] string? Type,
        [property: JsonPropertyName("nombre")] string? Name);
}
