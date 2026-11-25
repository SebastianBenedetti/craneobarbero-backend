using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CraneoBarbero.Api.Pricing;

public sealed class PricingSettings
{
    public const string SectionName = "Pricing";

    public string IndecApiUrl { get; set; } = "https://apis.datos.gob.ar/series/api/";
    public string IndecSeriesId { get; set; } = "148.3_INIVELNAL_DICI_M_26";
    public string FallbackApiUrl { get; set; } = "https://api.argentinadatos.com/v1/finanzas/indices/inflacion";
    public int TimeoutSeconds { get; set; } = 15;
    public int CacheHours { get; set; } = 12;
}

public sealed record MonthlyInflation(DateOnly Month, decimal Percentage);

public interface IInflationSource
{
    string Name { get; }

    Task<IReadOnlyList<MonthlyInflation>> GetMonthlyAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken);
}

public sealed class IndecInflationSource(HttpClient http, Microsoft.Extensions.Options.IOptions<PricingSettings> options) : IInflationSource
{
    public string Name => "indec_api";

    public async Task<IReadOnlyList<MonthlyInflation>> GetMonthlyAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var start = fromMonth.AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = toMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"series/?ids={Uri.EscapeDataString(settings.IndecSeriesId)}&start_date={start}&end_date={end}&format=json&limit=1000";
        using var document = JsonDocument.Parse(await http.GetStringAsync(url, cancellationToken));

        var levels = new SortedDictionary<DateOnly, decimal>();
        foreach (var row in document.RootElement.GetProperty("data").EnumerateArray())
        {
            if (row.GetArrayLength() < 2 || row[1].ValueKind != JsonValueKind.Number ||
                !DateOnly.TryParseExact(row[0].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                continue;
            }

            levels[new DateOnly(date.Year, date.Month, 1)] = row[1].GetDecimal();
        }

        var result = new List<MonthlyInflation>();
        foreach (var (month, level) in levels)
        {
            if (month < fromMonth || month > toMonth || !levels.TryGetValue(month.AddMonths(-1), out var previous) || previous <= 0)
            {
                continue;
            }

            result.Add(new MonthlyInflation(month, decimal.Round((level / previous - 1m) * 100m, 4)));
        }

        return result;
    }
}

public sealed class ArgentinaDatosInflationSource(HttpClient http) : IInflationSource
{
    public string Name => "argentinadatos";

    public async Task<IReadOnlyList<MonthlyInflation>> GetMonthlyAsync(DateOnly fromMonth, DateOnly toMonth, CancellationToken cancellationToken)
    {
        var items = await http.GetFromJsonAsync<List<Item>>("", cancellationToken) ?? [];
        return items
            .Where(item => item.Value is not null &&
                           DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            .Select(item =>
            {
                var date = DateOnly.ParseExact(item.Date!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new MonthlyInflation(new DateOnly(date.Year, date.Month, 1), decimal.Round(item.Value!.Value, 4));
            })
            .Where(rate => rate.Month >= fromMonth && rate.Month <= toMonth)
            .GroupBy(rate => rate.Month)
            .Select(group => group.Last())
            .OrderBy(rate => rate.Month)
            .ToList();
    }

    private sealed record Item(
        [property: JsonPropertyName("fecha")] string? Date,
        [property: JsonPropertyName("valor")] decimal? Value);
}
