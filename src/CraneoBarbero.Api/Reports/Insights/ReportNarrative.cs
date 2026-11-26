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
    IReadOnlyList<ReportStrategy> Strategies,
    string Reviews,
    bool AiGenerated);

public sealed record ReportStrategy(string Title, string Action, string Reason, string HowTo, string Measure);

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
          Cada estrategia tiene:
          title: un nombre corto de 3 a 8 palabras, empezando con un verbo.
          action: qué hacer, en 1 o 2 oraciones.
          reason: por qué conviene, en 1 o 2 oraciones, citando el dato que la justifica (porcentaje, día, franja, sucursal, servicio o producto).
          howTo: cómo aplicarla en concreto, en 2 a 3 oraciones (qué días u horarios, qué sucursal, qué producto o servicio, cómo difundirla y por cuánto tiempo).
          measure: qué resultado medir el mes siguiente y con qué meta aproximada, en 1 o 2 oraciones.
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
            ["strategies"] = new
            {
                type = "ARRAY",
                description = "Entre 2 y 4 estrategias concretas.",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["title"] = new { type = "STRING", description = "Nombre corto que empieza con un verbo." },
                        ["action"] = new { type = "STRING", description = "Qué hacer." },
                        ["reason"] = new { type = "STRING", description = "Por qué, con el dato que la justifica." },
                        ["howTo"] = new { type = "STRING", description = "Cómo aplicarla en concreto." },
                        ["measure"] = new { type = "STRING", description = "Qué resultado medir y con qué meta." },
                    },
                    required = new[] { "title", "action", "reason", "howTo", "measure" },
                },
            },
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
                maxOutputTokens = 4096,
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
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Summary)
                || parsed.Strategies?.Any(s => !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(s.Action)) != true)
            {
                return null;
            }

            return new ReportNarrative(
                parsed.Summary.Trim(),
                parsed.Occupancy?.Trim() ?? string.Empty,
                parsed.Strategies!
                    .Where(s => !string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(s.Action))
                    .Select(s => new ReportStrategy(s.Title!.Trim(), s.Action!.Trim(), s.Reason?.Trim() ?? string.Empty,
                        s.HowTo?.Trim() ?? string.Empty, s.Measure?.Trim() ?? string.Empty))
                    .Take(4)
                    .ToList(),
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

    private sealed record GeminiAnswer(string? Summary, string? Occupancy, List<GeminiStrategy>? Strategies, string? Reviews);

    private sealed record GeminiStrategy(string? Title, string? Action, string? Reason, string? HowTo, string? Measure);
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

        var strategies = new List<ReportStrategy>();
        var total = s.Occupancy.Percent ?? 0;
        var lowestDayName = lowestDay?.Label.ToLowerInvariant() ?? "día más flojo";
        var lowestPart = s.OccupancyByPart.Where(r => r.Percent is not null).OrderBy(r => r.Percent).FirstOrDefault();
        var lowestPartName = lowestPart?.Label.ToLowerInvariant() ?? "franja con menos turnos";
        var busiestBranch = s.OccupancyByBranch.Where(r => r.Percent is not null).OrderByDescending(r => r.Percent).FirstOrDefault();
        var totalText = Percent(s.Occupancy.Percent);
        if (total >= 85)
        {
            var branchName = busiestBranch?.Label ?? "la sucursal más ocupada";
            strategies.Add(new ReportStrategy(
                "Sumar horarios donde la agenda está llena",
                $"Abrir más horarios o sumar un barbero en {branchName}.",
                $"La ocupación total fue de {totalText}" + (busiestBranch is null ? "." : $" y {branchName} llegó a {Percent(busiestBranch.Percent)}, así que hay clientes que no consiguen turno."),
                $"Desde la Agenda, agregar horarios extra el {highestDay?.Label.ToLowerInvariant() ?? "día más pedido"} y en las horas con más turnos durante un mes. Si la demanda se sostiene, evaluar un barbero más a medio tiempo esos días.",
                "Medir cuántos de los horarios nuevos se reservan: la meta es que se ocupen más del 70 % sin que baje la ocupación del resto."));
            strategies.Add(new ReportStrategy(
                "Ajustar el precio de las franjas más pedidas",
                "Subir un poco el precio en los horarios con más demanda y mantenerlo en los más tranquilos.",
                $"Con {totalText} de ocupación la agenda casi no tiene lugar, y los horarios más pedidos se llenan igual.",
                "Aplicar un aumento chico (por ejemplo 5 a 10 %) solo en los servicios y horarios más pedidos y avisarlo con anticipación en el local y en las redes.",
                "Comparar el ingreso por turnos y la ocupación con el mes anterior: la meta es más ingreso sin perder más de 5 puntos de ocupación."));
        }
        else if (total >= 60)
        {
            strategies.Add(new ReportStrategy(
                $"Reforzar el {lowestDayName} con una promoción",
                $"Ofrecer un beneficio puntual para los turnos del {lowestDayName}.",
                $"La ocupación general es buena ({totalText}), pero el {lowestDayName} fue el día más flojo ({Percent(lowestDay?.Percent)}).",
                $"Armar un descuento chico o un servicio extra sin cargo para los turnos del {lowestDayName}, sobre todo en la {lowestPartName}, y difundirlo en redes y en el local durante cuatro semanas, sin publicidad general.",
                $"Medir la ocupación del {lowestDayName} al mes siguiente: la meta es subirla al menos 10 puntos."));
            strategies.Add(new ReportStrategy(
                "Ofrecer combos de corte y producto",
                "Vender un combo de un servicio con un producto de la tienda a un precio conjunto.",
                $"En el período se vendieron {s.Summary.Products.Units} productos por {Money(s.Summary.Products.Income)}, contra {appointments.Completed} turnos completados: hay margen para que más clientes se lleven algo.",
                "Elegir uno o dos productos que se usen en el corte (por ejemplo una pomada o un aceite para barba), que el barbero lo recomiende al terminar y mostrar el combo en el mostrador.",
                "Medir las unidades vendidas de esos productos y el ingreso por productos: la meta es crecer un 20 % el mes siguiente."));
        }
        else
        {
            strategies.Add(new ReportStrategy(
                $"Lanzar una promoción para el {lowestDayName}",
                $"Ofrecer un descuento concreto para los turnos del {lowestDayName}.",
                $"La ocupación fue baja ({totalText}) y el {lowestDayName} fue el día con menos turnos ({Percent(lowestDay?.Percent)}).",
                $"Publicar la promoción en redes y en el local durante cuatro semanas, con foco en la {lowestPartName}, y recordarla a los clientes que vinieron en los últimos meses.",
                $"Medir la ocupación del {lowestDayName} y los turnos nuevos: la meta es subirla al menos 15 puntos."));
            strategies.Add(new ReportStrategy(
                "Hacer publicidad acotada a la zona",
                "Invertir en publicidad paga cerca de cada sucursal durante un mes.",
                $"Con {totalText} de ocupación sobran horarios abiertos y faltan clientes nuevos.",
                "Usar un presupuesto chico en redes, segmentado a unas pocas cuadras de cada sucursal, con un mensaje simple que lleve a reservar el turno online.",
                "Medir cuántos clientes nuevos reservan en el mes y la ocupación total: la meta es superar el 60 %."));
        }

        var slow = s.Products.Products.OrderBy(p => p.Units).FirstOrDefault();
        if (slow is not null)
        {
            strategies.Add(new ReportStrategy(
                $"Impulsar {slow.Name}",
                $"Dar más visibilidad a {slow.Name} con una recomendación o un descuento.",
                $"Fue el producto que menos se vendió en el período ({slow.Units} unidades).",
                "Ubicarlo a la vista en el mostrador, que los barberos lo recomienden cuando corresponda y ofrecer un descuento corto o sumarlo a un combo con un servicio.",
                "Medir las unidades vendidas al mes siguiente: la meta es al menos duplicarlas o, si no se mueve, dejar de reponerlo."));
        }

        var reviews = s.Reviews.Count == 0
            ? "No hubo reseñas en el período."
            : $"Hubo {s.Reviews.Count} reseñas, con un promedio de {s.Reviews.AverageBarber:0.0} para los barberos y {s.Reviews.AverageCut:0.0} para los servicios.";
        return new ReportNarrative(summary, occupancy, strategies, reviews, AiGenerated: false);
    }

    private static string Join(IReadOnlyList<OccupancyRow> rows) =>
        rows.Count == 0 ? "sin datos" : string.Join("; ", rows.Select(r => $"{r.Label} {Percent(r.Percent)}"));
}
