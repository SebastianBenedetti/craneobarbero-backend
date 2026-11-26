using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CraneoBarbero.Api.Assistant;

public static partial class TextMatcher
{
    private static readonly string[] Weekdays = ["domingo", "lunes", "martes", "miercoles", "jueves", "viernes", "sabado"];
    private static readonly string[] OtherIntentWords = ["cancel", "modific", "reprogram", "mis turnos", "reembols", "devolu", "anular"];
    private static readonly string[] YesWords = ["si", "confirmo", "confirmar", "dale", "ok", "listo", "de acuerdo", "perfecto"];
    private static readonly string[] RestartWords = ["empezar de nuevo", "reiniciar", "otra vez desde el principio", "empezar otra vez"];
    private static readonly string[] IgnoredNameWords = ["sucursal", "de", "del", "la", "el", "y"];

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is ':' or '/' ? character : ' ');
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    public static bool IsYes(string normalized) => YesWords.Any(word => HasPhrase(normalized, word));

    public static bool IsRestart(string normalized) => RestartWords.Any(word => HasPhrase(normalized, word));

    public static AssistantInterpretation Interpret(string text, AssistantCatalog catalog, DateOnly today, int? currentBranchId)
    {
        var normalized = Normalize(text);
        if (OtherIntentWords.Any(word => normalized.Contains(word, StringComparison.Ordinal)))
        {
            return new AssistantInterpretation(OtherIntent: true);
        }

        var branchId = MatchBranch(normalized, catalog);
        var barberId = MatchBarber(normalized, catalog, branchId ?? currentBranchId);
        var cutId = MatchCut(normalized, catalog);
        var (part, withoutPart) = ExtractPart(normalized);
        var date = ExtractDate(withoutPart, today);
        var time = ExtractTime(withoutPart);
        if (time is { } exact && part is null)
        {
            part = exact.Hour < 13 ? DayPart.Morning : DayPart.Afternoon;
        }

        return new AssistantInterpretation(false, branchId, barberId, cutId, date, time, part);
    }

    public static int? MatchBranch(string normalized, AssistantCatalog catalog, bool allowPartial = false)
    {
        var matches = catalog.Branches
            .Where(branch => NameMatches(normalized, branch.Name, allowPartial))
            .Select(branch => branch.Id)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static int? MatchBarber(string normalized, AssistantCatalog catalog, int? branchId, bool allowPartial = false)
    {
        var candidates = catalog.Barbers
            .Where(barber => FullNameMatches(normalized, barber.Name) || FirstNameMatches(normalized, barber.Name) ||
                             (allowPartial && NameMatches(normalized, barber.Name, true)))
            .ToList();
        if (candidates.Count > 1 && branchId is not null)
        {
            candidates = candidates.Where(barber => barber.BranchId == branchId).ToList();
        }

        if (candidates.Count > 1)
        {
            var full = candidates.Where(barber => FullNameMatches(normalized, barber.Name)).ToList();
            if (full.Count == 1)
            {
                candidates = full;
            }
        }

        return candidates.Count == 1 ? candidates[0].Id : null;
    }

    public static int? MatchCut(string normalized, AssistantCatalog catalog, bool allowPartial = false)
    {
        var matches = catalog.Cuts
            .Select(cut => (cut.Id, Words: SignificantWords(cut.Name)))
            .Where(cut => cut.Words.Count > 0 && (cut.Words.All(word => HasPhrase(normalized, word)) ||
                                                  (allowPartial && HasPhrase(string.Join(' ', cut.Words), normalized))))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var longest = matches.Max(match => match.Words.Count);
        var best = matches.Where(match => match.Words.Count == longest).ToList();
        return best.Count == 1 ? best[0].Id : null;
    }

    public static DateOnly? ParseIsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static TimeOnly? ParseTime(string? value) =>
        TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    private static (DayPart? Part, string Remaining) ExtractPart(string normalized)
    {
        var morning = MorningPhrase().Match(normalized);
        if (morning.Success)
        {
            return (DayPart.Morning, MorningPhrase().Replace(normalized, " "));
        }

        if (HasPhrase(normalized, "tarde") || HasPhrase(normalized, "tardecita"))
        {
            return (DayPart.Afternoon, normalized);
        }

        return (null, normalized);
    }

    private static DateOnly? ExtractDate(string normalized, DateOnly today)
    {
        if (HasPhrase(normalized, "pasado manana"))
        {
            return today.AddDays(2);
        }

        if (HasPhrase(normalized, "manana"))
        {
            return today.AddDays(1);
        }

        if (HasPhrase(normalized, "hoy"))
        {
            return today;
        }

        var numeric = NumericDate().Match(normalized);
        if (numeric.Success)
        {
            var day = int.Parse(numeric.Groups["day"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(numeric.Groups["month"].Value, CultureInfo.InvariantCulture);
            if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
            {
                var candidate = new DateOnly(today.Year, month, day);
                if (candidate < today && day <= DateTime.DaysInMonth(today.Year + 1, month))
                {
                    candidate = new DateOnly(today.Year + 1, month, day);
                }

                return candidate;
            }
        }

        for (var index = 0; index < Weekdays.Length; index++)
        {
            if (HasPhrase(normalized, Weekdays[index]))
            {
                var offset = (index - (int)today.DayOfWeek + 7) % 7;
                return today.AddDays(offset);
            }
        }

        return null;
    }

    private static TimeOnly? ExtractTime(string normalized)
    {
        var match = ClockTime().Match(normalized);
        int hour;
        var minute = 0;
        if (match.Success)
        {
            hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
        }
        else
        {
            match = HourOnly().Match(normalized);
            if (!match.Success)
            {
                return null;
            }

            hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
        }

        if (hour is >= 1 and <= 7)
        {
            hour += 12;
        }

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59 ? new TimeOnly(hour, minute) : null;
    }

    private static bool NameMatches(string normalized, string name, bool allowPartial)
    {
        var words = SignificantWords(name);
        if (words.Count == 0)
        {
            return false;
        }

        var phrase = string.Join(' ', words);
        return HasPhrase(normalized, phrase) || (allowPartial && normalized.Length >= 3 && HasPhrase(phrase, normalized));
    }

    private static bool FullNameMatches(string normalized, string name)
    {
        var phrase = Normalize(name);
        return phrase.Length > 0 && HasPhrase(normalized, phrase);
    }

    private static bool FirstNameMatches(string normalized, string name)
    {
        var first = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is { Length: >= 3 } && HasPhrase(normalized, first);
    }

    private static List<string> SignificantWords(string name) =>
        Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !IgnoredNameWords.Contains(word))
            .ToList();

    private static bool HasPhrase(string text, string phrase) =>
        phrase.Length > 0 && Regex.IsMatch(text, $@"(^|\s){Regex.Escape(phrase)}($|\s)");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(a|por|de|en) la manana\b|\bmananita\b")]
    private static partial Regex MorningPhrase();

    [GeneratedRegex(@"\b(?<day>\d{1,2})/(?<month>\d{1,2})\b")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"\b(?<hour>\d{1,2}):(?<minute>\d{2})\b")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\b(a las|tipo|las)\s+(?<hour>\d{1,2})\b|\b(?<hour>\d{1,2})\s*(hs|h|horas)\b")]
    private static partial Regex HourOnly();
}
