using System.Globalization;
using System.Text;
using CraneoBarbero.Api.Configuration;
using CraneoBarbero.Api.Reports.Export;

namespace CraneoBarbero.Api.Reports.Insights;

public sealed record AiReportResult(ExportedFile File, ReportNarrative Narrative);

public sealed class AiReportQuota(BusinessClock clock)
{
    public const int ManualPerDay = 10;

    private readonly Lock _lock = new();
    private DateOnly _day;
    private int _used;

    public bool TryUse()
    {
        lock (_lock)
        {
            var today = clock.Today;
            if (_day != today)
            {
                _day = today;
                _used = 0;
            }

            if (_used >= ManualPerDay)
            {
                return false;
            }

            _used++;
            return true;
        }
    }
}

public sealed class AiReportService(InsightData data, IReportWriter writer)
{
    public const string Title = "Reporte mensual Cráneo Barbero";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public static string FileName(ReportScope scope) => ReportNames.FileName(scope, "pdf");

    public async Task<AiReportResult> GenerateAsync(ReportScope scope, bool useAi, CancellationToken cancellationToken)
    {
        var snapshot = await data.BuildAsync(scope, cancellationToken);
        var narrative = (useAi ? await writer.WriteAsync(InsightText.Describe(snapshot), cancellationToken) : null)
                        ?? InsightText.RuleBased(snapshot);
        var pdf = Build(snapshot, narrative);
        return new AiReportResult(new ExportedFile(pdf, "application/pdf", FileName(scope)), narrative);
    }

    private static byte[] Build(InsightSnapshot s, ReportNarrative narrative)
    {
        var period = ReportNames.Period(s.Scope);
        var pdf = new SimplePdf(Title, [$"Período: {period}", $"Alcance: {s.ScopeName}"], $"Cráneo Barbero · Reporte mensual {period} · {s.ScopeName}");
        var rating = s.Reviews.AverageBarber is { } average ? average.ToString("0.0", Spanish) + " / 5" : "Sin reseñas";
        pdf.Stats(
        [
            new("Ingreso total", InsightText.Money(s.Summary.TotalIncome), "Turnos y productos"),
            new("Turnos completados", s.Summary.Appointments.Completed.ToString(Spanish), $"{s.Services.Count} servicios"),
            new("Ocupación de la agenda", InsightText.Percent(s.Occupancy.Percent), "Reservados sobre abiertos"),
            new("Puntaje de los barberos", rating, $"{s.Reviews.Count} reseñas"),
        ]);

        pdf.Heading("Resumen del período", 40f);
        pdf.Paragraph(Clean(narrative.Summary), 9.5f, dark: true, before: 4f);

        pdf.Heading("Ocupación de turnos", 40f);
        pdf.Paragraph(Clean(narrative.Occupancy), 9.5f, dark: true, before: 4f);
        var occupancyRows = s.OccupancyByBranch.Select(r => Row("Sucursal", r))
            .Concat(s.OccupancyByWeekday.Select(r => Row("Día", r)))
            .Concat(s.OccupancyByPart.Select(r => Row("Franja", r)))
            .ToList();
        if (occupancyRows.Count > 0)
        {
            pdf.Table([new("Tipo", 1f), new("Detalle", 2.4f), new("Abiertos", 1f, true), new("Reservados", 1.1f, true), new("Ocupación", 1.1f, true)],
                occupancyRows);
        }

        pdf.Heading("Estrategias recomendadas", 40f);
        var number = 1;
        var names = ProperWords(s);
        foreach (var strategy in narrative.Strategies)
        {
            pdf.Paragraph($"{number++}. {Clean(strategy.Title)}", 10.5f, dark: true, before: 10f);
            pdf.Paragraph($"Qué hacer: {AfterColon(Clean(strategy.Action), names)}", 9.5f, dark: true, before: 3f);
            if (!string.IsNullOrWhiteSpace(strategy.Reason))
            {
                pdf.Paragraph($"Por qué: {AfterColon(Clean(strategy.Reason), names)}", 9.5f, dark: true, before: 3f);
            }

            if (!string.IsNullOrWhiteSpace(strategy.HowTo))
            {
                pdf.Paragraph($"Cómo aplicarla: {AfterColon(Clean(strategy.HowTo), names)}", 9.5f, dark: true, before: 3f);
            }

            if (!string.IsNullOrWhiteSpace(strategy.Measure))
            {
                pdf.Paragraph($"Qué medir: {AfterColon(Clean(strategy.Measure), names)}", 9.5f, dark: true, before: 3f);
            }
        }

        pdf.Heading("Reseñas", 40f);
        pdf.Paragraph(Clean(narrative.Reviews), 9.5f, dark: true, before: 4f);

        pdf.Paragraph(narrative.AiGenerated
            ? "Análisis redactado con inteligencia artificial (Gemini) a partir de los datos del sistema. Los números salen de la base; revisá las recomendaciones antes de aplicarlas."
            : "Análisis armado con reglas automáticas a partir de los datos del sistema, porque el análisis con inteligencia artificial no estaba disponible.");
        return pdf.Build();
    }

    private static string[] Row(string kind, OccupancyRow row) =>
        [kind, row.Label, row.OpenSlots.ToString("N0", Spanish), row.BusySlots.ToString("N0", Spanish), InsightText.Percent(row.Percent)];

    public static IReadOnlySet<string> ProperWords(InsightSnapshot s) =>
        s.Barbers.Select(b => b.Name)
            .Concat(s.Branches.Select(b => b.Name))
            .Concat(s.Services.Select(x => x.Name))
            .Concat(s.Products.Products.Select(p => p.Name))
            .Append("Cráneo Barbero")
            .SelectMany(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(word => word.Length > 0 && char.IsUpper(word[0]))
            .ToHashSet(StringComparer.Ordinal);

    public static string AfterColon(string text, IReadOnlySet<string> properWords)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length < 2 || !char.IsUpper(trimmed[0]) || char.IsUpper(trimmed[1]))
        {
            return trimmed;
        }

        var firstWord = new string(trimmed.TakeWhile(char.IsLetter).ToArray());
        return properWords.Contains(firstWord) ? trimmed : char.ToLower(trimmed[0], Spanish) + trimmed[1..];
    }

    public static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormC))
        {
            switch (character)
            {
                case '“' or '”' or '«' or '»':
                    builder.Append('"');
                    break;
                case '‘' or '’':
                    builder.Append('\'');
                    break;
                case '–' or '—' or '•':
                    builder.Append('-');
                    break;
                case '…':
                    builder.Append("...");
                    break;
                case '\r' or '\n' or '\t':
                    builder.Append(' ');
                    break;
                default:
                    if (character <= 'ÿ')
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString().Trim();
    }
}
