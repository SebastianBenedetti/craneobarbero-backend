using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace CraneoBarbero.Api.Assistant;

public sealed record InterpretedText(
    string? Intent,
    string? Branch,
    string? Barber,
    string? Service,
    string? Date,
    string? Time,
    string? DayPart);

public interface ITextInterpreter
{
    bool IsConfigured { get; }

    Task<InterpretedText?> InterpretAsync(string text, AssistantCatalog catalog, DateOnly today, CancellationToken cancellationToken);
}

public sealed class GeminiInterpreter(HttpClient http, IOptions<AssistantSettings> options, ILogger<GeminiInterpreter> logger) : ITextInterpreter
{
    private static readonly string[] Weekdays = ["domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.GeminiApiKey);

    public async Task<InterpretedText?> InterpretAsync(
        string text, AssistantCatalog catalog, DateOnly today, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var settings = options.Value;
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = Instructions(catalog, today) } } },
            contents = new[] { new { role = "user", parts = new[] { new { text } } } },
            generationConfig = new
            {
                temperature = 0,
                maxOutputTokens = 200,
                responseMimeType = "application/json",
                responseSchema = Schema,
            },
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{Uri.EscapeDataString(settings.GeminiModel)}:generateContent")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Gemini respondió {StatusCode}; el asistente sigue por botones.", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var answer = document.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();
            return string.IsNullOrWhiteSpace(answer) ? null : JsonSerializer.Deserialize<InterpretedText>(answer, Json);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("No se pudo interpretar el mensaje con Gemini ({Error}); el asistente sigue por botones.", exception.GetType().Name);
            return null;
        }
    }

    private static readonly object Schema = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["intent"] = new { type = "STRING", @enum = new[] { "book", "other" } },
            ["branch"] = new { type = "STRING", nullable = true },
            ["barber"] = new { type = "STRING", nullable = true },
            ["service"] = new { type = "STRING", nullable = true },
            ["date"] = new { type = "STRING", nullable = true },
            ["time"] = new { type = "STRING", nullable = true },
            ["dayPart"] = new { type = "STRING", nullable = true, @enum = new[] { "morning", "afternoon" } },
        },
        required = new[] { "intent" },
    };

    private static string Instructions(AssistantCatalog catalog, DateOnly today)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Interpretás mensajes de clientes que quieren sacar un turno en una barbería de Argentina.");
        builder.AppendLine("Respondé solo con el JSON pedido. No inventes valores: usá exactamente un nombre de las listas o null.");
        builder.AppendLine("Si el mensaje no es para reservar un turno (por ejemplo, cancelar, modificar o consultar turnos), intent = \"other\".");
        builder.AppendLine("Los apodos o nombres parecidos se pueden asociar al barbero más parecido de la lista.");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"Hoy es {Weekdays[(int)today.DayOfWeek]} {today:yyyy-MM-dd}. La fecha va como AAAA-MM-DD y la hora como HH:mm (24 h).");
        builder.AppendLine("dayPart: \"morning\" (turnos hasta las 13:30) o \"afternoon\" (desde las 14:00), solo si el cliente lo dice.");
        builder.AppendLine("Sucursales: " + string.Join("; ", catalog.Branches.Select(b => b.Name)));
        builder.AppendLine("Barberos: " + string.Join("; ", catalog.Barbers.Select(b =>
            $"{b.Name} ({catalog.Branches.FirstOrDefault(branch => branch.Id == b.BranchId)?.Name})")));
        builder.AppendLine("Servicios: " + string.Join("; ", catalog.Cuts.Select(c => c.Name)));
        return builder.ToString();
    }
}
