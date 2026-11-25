using System.Globalization;
using CraneoBarbero.Api.Common.Errors;
using CraneoBarbero.Api.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Pricing;

public sealed record InflationMonthResponse(string Month, decimal Percentage);

public sealed record InflationSuggestionResponse(
    bool Available,
    string? Source,
    string? From,
    string? To,
    decimal? Percentage,
    IReadOnlyList<InflationMonthResponse> Months,
    string? Message);

public sealed class InflationService(
    IEnumerable<IInflationSource> sources,
    IMemoryCache cache,
    BusinessClock clock,
    IOptions<PricingSettings> options,
    ILogger<InflationService> logger)
{
    public const int MaxMonths = 24;
    private static readonly DateOnly FirstMonth = new(2017, 1, 1);
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public static DateOnly? ParseMonth(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateOnly.TryParseExact(value.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            throw new BadRequestException($"El mes {field} debe tener el formato AAAA-MM.");
        }

        return month;
    }

    public async Task<InflationSuggestionResponse> SuggestAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var currentMonth = new DateOnly(clock.Today.Year, clock.Today.Month, 1);
        if (from is not null || to is not null)
        {
            var start = from ?? to!.Value;
            var end = to ?? from!.Value;
            if (start > end)
            {
                throw new BadRequestException("El mes inicial no puede ser posterior al final.");
            }

            if (start < FirstMonth || end > currentMonth)
            {
                throw new BadRequestException("Elegí meses entre enero de 2017 y el mes actual.");
            }

            if (MonthsBetween(start, end) > MaxMonths)
            {
                throw new BadRequestException($"Se pueden acumular hasta {MaxMonths} meses.");
            }

            return await CachedAsync(start, end, cancellationToken);
        }

        var latest = await LatestAvailableAsync(currentMonth, cancellationToken);
        return latest is null
            ? Unavailable(null, null, "No se pudo consultar la inflación del INDEC. Ingresá el porcentaje a mano.")
            : await CachedAsync(latest.Value, latest.Value, cancellationToken);
    }

    private async Task<DateOnly?> LatestAvailableAsync(DateOnly currentMonth, CancellationToken cancellationToken)
    {
        var key = $"inflation:latest:{currentMonth:yyyy-MM}";
        if (cache.TryGetValue(key, out DateOnly cached))
        {
            return cached;
        }

        foreach (var source in sources)
        {
            var rates = await TryGetAsync(source, currentMonth.AddMonths(-3), currentMonth, cancellationToken);
            if (rates is { Count: > 0 })
            {
                var latest = rates.Max(r => r.Month);
                cache.Set(key, latest, TimeSpan.FromHours(options.Value.CacheHours));
                return latest;
            }
        }

        return null;
    }

    private async Task<InflationSuggestionResponse> CachedAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var key = $"inflation:{from:yyyy-MM}:{to:yyyy-MM}";
        if (cache.TryGetValue(key, out InflationSuggestionResponse? cached) && cached is not null)
        {
            return cached;
        }

        var expected = MonthsBetween(from, to);
        var anyAnswer = false;
        foreach (var source in sources)
        {
            var rates = await TryGetAsync(source, from, to, cancellationToken);
            if (rates is null)
            {
                continue;
            }

            anyAnswer = true;
            if (rates.Count != expected)
            {
                continue;
            }

            var accumulated = rates.Aggregate(1m, (total, rate) => total * (1m + rate.Percentage / 100m));
            var response = new InflationSuggestionResponse(
                true,
                source.Name,
                Format(from),
                Format(to),
                decimal.Round((accumulated - 1m) * 100m, 4),
                rates.Select(r => new InflationMonthResponse(Format(r.Month), r.Percentage)).ToList(),
                null);
            cache.Set(key, response, TimeSpan.FromHours(options.Value.CacheHours));
            return response;
        }

        return Unavailable(from, to, anyAnswer
            ? $"El INDEC todavía no publicó la inflación de {Describe(to)}. Ingresá el porcentaje a mano."
            : "No se pudo consultar la inflación del INDEC. Ingresá el porcentaje a mano.");
    }

    private async Task<IReadOnlyList<MonthlyInflation>?> TryGetAsync(
        IInflationSource source, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetMonthlyAsync(from, to, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "No se pudo consultar la inflación en {Source}", source.Name);
            return null;
        }
    }

    private static InflationSuggestionResponse Unavailable(DateOnly? from, DateOnly? to, string message) =>
        new(false, null, from is null ? null : Format(from.Value), to is null ? null : Format(to.Value), null, [], message);

    private static int MonthsBetween(DateOnly from, DateOnly to) => (to.Year - from.Year) * 12 + to.Month - from.Month + 1;

    private static string Format(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string Describe(DateOnly month) => month.ToString("MMMM 'de' yyyy", Spanish);
}
