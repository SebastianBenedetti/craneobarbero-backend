namespace CraneoBarbero.Api.Assistant;

public sealed record AssistantProduct(int Id, string Name, decimal Price, bool InStock);

public sealed record CatalogProduct(int Id, string Name, decimal Price, bool InStock);

public static class ProductLookup
{
    public const int MaxResults = 5;

    private static readonly string[] ProductWords =
    [
        "precio", "precios", "cuanto", "cuesta", "cuestan", "sale", "salen", "vale", "valen", "venden", "vendes",
        "tienen", "tenes", "hay", "producto", "productos", "comprar", "compro", "stock", "tienda",
    ];

    private static readonly HashSet<string> IgnoredWords =
    [
        "para", "con", "sin", "del", "los", "las", "una", "uno", "unos", "unas", "que", "por", "mas", "muy",
        "precio", "cuanto", "cuesta", "sale", "vale", "tienen", "producto", "productos", "turno", "corte",
    ];

    public static bool AsksAboutProducts(string normalized) =>
        Words(normalized).Any(word => ProductWords.Contains(word));

    public static bool NamesTheStore(string normalized) =>
        Words(normalized).Any(word => word is "producto" or "productos" or "comprar" or "compro" or "tienda" or "venden" or "vendes" or "stock");

    public static bool MentionsAppointment(string normalized) =>
        Words(normalized).Any(word => word is "turno" or "turnos" or "reservar" or "reserva" or "sacar");

    public static (IReadOnlyList<CatalogProduct> Products, int Score) Find(string normalized, IReadOnlyList<CatalogProduct> products)
    {
        var messageWords = Words(normalized).Where(word => word.Length >= 3).ToList();
        if (messageWords.Count == 0)
        {
            return ([], 0);
        }

        var scored = products
            .Select(product => (Product: product, Score: Score(messageWords, product.Name)))
            .Where(item => item.Score > 0)
            .ToList();
        if (scored.Count == 0)
        {
            return ([], 0);
        }

        var best = scored.Max(item => item.Score);
        var found = scored
            .Where(item => item.Score == best)
            .Select(item => item.Product)
            .OrderBy(product => product.Name)
            .Take(MaxResults)
            .ToList();
        return (found, best);
    }

    private static int Score(List<string> messageWords, string productName)
    {
        var nameWords = Words(TextMatcher.Normalize(productName))
            .Where(word => word.Length >= 4 && !IgnoredWords.Contains(word))
            .Distinct()
            .ToList();
        return nameWords.Count(nameWord => messageWords.Any(word => SameWord(word, nameWord)));
    }

    private static bool SameWord(string word, string nameWord)
    {
        if (word == nameWord)
        {
            return true;
        }

        var shorter = word.Length < nameWord.Length ? word : nameWord;
        var longer = word.Length < nameWord.Length ? nameWord : word;
        return shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal) && longer.Length - shorter.Length <= 2;
    }

    private static IEnumerable<string> Words(string normalized) =>
        normalized.Split([' ', ',', '.', '?', '¿', '!', '¡', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
}
