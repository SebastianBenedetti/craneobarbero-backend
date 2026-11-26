using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CraneoBarbero.Api.Assistant;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Reports.Insights;

public sealed record ReportNarrative(
    string Summary,
    string Occupancy,
    IReadOnlyList<string> Strategies,
    string Reviews,
    bool AiGenerated);

public interface IReportWriter
{
    bool IsConfigured { get; }

    Task<ReportNarrative?> WriteAsync(string data, CancellationToken cancellationToken);
}

public sealed class GeminiReportWriter(HttpClient http, IOptions<AssistantSettings> options, ILogger<GeminiReportWriter> logger) : IReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Instructions = """
        Sos un analista de negocio de una barbería de Rosario (Argentina) llamada Cráneo Barbero.
        Recibís los datos ya calculados de un período y escribís un informe para los dueños, en español rioplatense, claro y directo.
        Reglas:
        - Usá solo los datos recibidos. No inventes números, nombres, competidores, precios de otros locales ni datos de la zona.
        - Mencioná los números importantes (porcentajes de ocupación, días y horarios más pedidos, sucursales, servicios, productos).
        - Ocupación: 85 % o más es alta (no hace falta publicidad; evaluá sumar horarios o un barbero, o ajustar precios en las franjas llenas);
          entre 60 % y 85 % es buena (reforzá los días y franjas flojos); menos de 60 % es baja (proponé promociones o publicidad concretas
          para los días y franjas con menos turnos).
        - Proponé siempre entre 2 y 4 estrategias concretas y accionables, también si la barbería anda bien (por ejemplo combos de corte
          y producto, promociones en franjas flojas, fidelización, impulsar productos que rotan poco, sumar o reorganizar barberos).
          Cada estrategia en una o dos oraciones, empezando con un verbo.
        - Si hay reseñas, resumí qué se valora y qué se critica, sin citar nombres de clientes. Si no hay, decilo en una oración.
        - Sin markdown, sin viñetas dentro de los textos, sin emojis. Texto plano.
        """;

    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["summary"] = new { type = "STRING", description = "Resumen general del período, 3 a 5 oraciones." },
            ["occupancy"] = new { type = "STRING", description = "Análisis de ocupación por sucursal, días y horarios, 3 a 6 oraciones." },
            ["strategies"] = new { type = "ARRAY", items = new { type = "STRING" }, description = "Entre 2 y 4 estrategias concretas." },
            ["reviews"] = new { type = "STRING", description = "Resumen de las reseñas, 2 a 4 oraciones." },
        },
        required = new[] { "summary", "occupancy", "strategies", "reviews" },
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.GeminiApiKey);

    public async Task<ReportNarrative?> WriteAsync(string data, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var settings = options.Value;
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = Instructions } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = data } } } },
            generationConfig = new
            {
                temperature = 0.4,
                maxOutputTokens = 2000,
                responseMimeType = "application/json",
                responseSchema = Schema,
            },
        };

        try
        {
            using var response = await SendWithRetryAsync(body, settings, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini respondió {StatusCode}; el reporte se arma con reglas.", (int)response.StatusCode);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var answer = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            var parsed = string.IsNullOrWhiteSpace(answer) ? null : JsonSerializer.Deserialize<GeminiAnswer>(answer, Json);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Summary) || parsed.Strategies is not { Count: > 0 })
            {
                return null;
            }

            return new ReportNarrative(
                parsed.Summary.Trim(),
                parsed.Occupancy?.Trim() ?? string.Empty,
                parsed.Strategies.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(4).ToList(),
                parsed.Reviews?.Trim() ?? string.Empty,
                AiGenerated: true);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("No se pudo generar el análisis con Gemini ({Error}); el reporte se arma con reglas.", exception.GetType().Name);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(object body, AssistantSettings settings, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(settings.GeminiModel)}:generateContent")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);
            var response = await http.SendAsync(request, cancellationToken);
            var busy = response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests;
            if (!busy || attempt >= 3)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
        }
    }

    private sealed record GeminiAnswer(string? Summary, string? Occupancy, List<string>? Strategies, string? Reviews);
}

public static class InsightText
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public static string Money(decimal amount) => "$" + amount.ToString("N0", Spanish);

    public static string Percent(decimal? value) => value is null ? "sin horarios" : value.Value.ToString("0.#", Spanish) + " %";

    public static string Describe(InsightSnapshot s)
    {
        var builder = new StringBuilder();
        var appointments = s.Summary.Appointments;
        builder.AppendLine($"Período: {s.Scope.From:dd/MM/yyyy} al {s.Scope.To:dd/MM/yyyy}. Alcance: {s.ScopeName}.");
        builder.AppendLine($"Horario de atención: lunes a viernes de 9 a 13 y de 15 a 20; sábados de 9 a 15. Turnos de 30 minutos.");
        builder.AppendLine($"Ingreso total: {Money(s.Summary.TotalIncome)} (turnos {Money(appointments.Income)}, productos {Money(s.Summary.Products.Income)}, ganancia de productos {Money(s.Summary.Products.Profit)}).");
        builder.AppendLine($"Turnos completados: {appointments.Completed}. Cancelaciones tardías cobradas: {appointments.LateCancellationsCharged}.");
        builder.AppendLine("Turnos por estado: " + string.Join(", ", appointments.ByStatus.Select(p => $"{p.Key} {p.Value}")) + ".");
        builder.AppendLine($"Ocupación total de la agenda (horarios reservados sobre horarios abiertos, hasta hoy): {Percent(s.Occupancy.Percent)}.");
        builder.AppendLine("Ocupación por sucursal: " + Join(s.OccupancyByBranch) + ".");
        builder.AppendLine("Ocupación por día: " + Join(s.OccupancyByWeekday) + ".");
        builder.AppendLine("Ocupación por franja: " + Join(s.OccupancyByPart) + ".");
        if (s.BusiestHours.Count > 0)
        {
            builder.AppendLine("Horarios con más turnos: " + string.Join(", ", s.BusiestHours.Select(h => $"{h.Hour}:00 ({h.Count})")) + ".");
        }

        builder.AppendLine("Ingresos por mes: " + string.Join("; ", s.Months.Select(m => $"{m.Period} {Money(m.TotalIncome)}")) + ".");
        builder.AppendLine("Por sucursal: " + string.Join("; ", s.Branches.Select(b =>
            $"{b.Name}: {b.AppointmentsCompleted} turnos, turnos {Money(b.AppointmentIncome)}, productos {Money(b.ProductIncome)}")) + ".");
        builder.AppendLine("Barberos: " + string.Join("; ", s.Barbers.Select(b =>
            $"{b.Name} ({b.BranchName ?? "varias"}): {b.Completed} turnos, ocupación {Percent(b.Occupancy)}, puntaje {(b.Rating?.ToString("0.0", Spanish) ?? "sin reseñas")}")) + ".");
        builder.AppendLine("Servicios: " + string.Join("; ", s.Services.Select(x => $"{x.Name}: {x.Completed} ({Money(x.Income)})")) + ".");
        var products = s.Products.Products;
        builder.AppendLine("Productos más vendidos: " + string.Join("; ", products.OrderByDescending(p => p.Units).Take(5).Select(p => $"{p.Name}: {p.Units} u.")) + ".");
        builder.AppendLine("Productos que menos se vendieron: " + string.Join("; ", products.OrderBy(p => p.Units).Take(5).Select(p => $"{p.Name}: {p.Units} u.")) + ".");
        builder.AppendLine($"Reseñas: {s.Reviews.Count}, promedio barbero {s.Reviews.AverageBarber?.ToString("0.0", Spanish) ?? "-"}, promedio servicio {s.Reviews.AverageCut?.ToString("0.0", Spanish) ?? "-"}.");
        if (s.Reviews.Comments.Count > 0)
        {
            builder.AppendLine("Comentarios de las reseñas:");
            foreach (var comment in s.Reviews.Comments)
            {
                builder.AppendLine("- " + comment);
            }
        }

        return builder.ToString();
    }

    public static ReportNarrative RuleBased(InsightSnapshot s)
    {
        var appointments = s.Summary.Appointments;
        var summary = $"En el período se completaron {appointments.Completed} turnos y el ingreso total fue de {Money(s.Summary.TotalIncome)} " +
                      $"({Money(appointments.Income)} por turnos y {Money(s.Summary.Products.Income)} por productos).";
        var topService = s.Services.OrderByDescending(x => x.Completed).FirstOrDefault();
        if (topService is not null)
        {
            summary += $" El servicio más pedido fue {topService.Name} ({topService.Completed}).";
        }

        var lowestDay = s.OccupancyByWeekday.Where(r => r.Percent is not null).OrderBy(r => r.Percent).FirstOrDefault();
        var highestDay = s.OccupancyByWeekday.Where(r => r.Percent is not null).OrderByDescending(r => r.Percent).FirstOrDefault();
        var occupancy = $"La ocupación de la agenda fue de {Percent(s.Occupancy.Percent)}.";
        if (highestDay is not null && lowestDay is not null && highestDay != lowestDay)
        {
            occupancy += $" El día más ocupado fue el {highestDay.Label.ToLowerInvariant()} ({Percent(highestDay.Percent)}) y el más flojo, el {lowestDay.Label.ToLowerInvariant()} ({Percent(lowestDay.Percent)}).";
        }

        var strategies = new List<string>();
        var total = s.Occupancy.Percent ?? 0;
        if (total >= 85)
        {
            strategies.Add("Evaluar sumar horarios o un barbero en la sucursal más ocupada, porque la agenda está casi llena.");
            strategies.Add("Ajustar el precio de las franjas más pedidas y mantener el actual en las más tranquilas.");
        }
        else if (total >= 60)
        {
            strategies.Add($"Reforzar el {lowestDay?.Label.ToLowerInvariant() ?? "día más flojo"} con una promoción puntual, sin hacer publicidad general.");
            strategies.Add("Ofrecer un combo de corte con un producto de la tienda para subir el ticket promedio.");
        }
        else
        {
            strategies.Add($"Lanzar una promoción para el {lowestDay?.Label.ToLowerInvariant() ?? "día más flojo"} y difundirla en redes y en el local.");
            strategies.Add("Hacer publicidad paga acotada a la zona de cada sucursal durante el próximo mes y medir los turnos nuevos.");
        }

        var slow = s.Products.Products.OrderBy(p => p.Units).FirstOrDefault();
        if (slow is not null)
        {
            strategies.Add($"Impulsar {slow.Name}, que es el producto que menos se vendió, con un descuento o recomendándolo después del corte.");
        }

        var reviews = s.Reviews.Count == 0
            ? "No hubo reseñas en el período."
            : $"Hubo {s.Reviews.Count} reseñas, con un promedio de {s.Reviews.AverageBarber:0.0} para los barberos y {s.Reviews.AverageCut:0.0} para los servicios.";
        return new ReportNarrative(summary, occupancy, strategies, reviews, AiGenerated: false);
    }

    private static string Join(IReadOnlyList<OccupancyRow> rows) =>
        rows.Count == 0 ? "sin datos" : string.Join("; ", rows.Select(r => $"{r.Label} {Percent(r.Percent)}"));
}
