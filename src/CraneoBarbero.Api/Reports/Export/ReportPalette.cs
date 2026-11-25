namespace CraneoBarbero.Api.Reports.Export;

public sealed record ChartSlice(string Label, decimal Value, string Display);

public sealed record ChartData(string Title, IReadOnlyList<ChartSlice> Slices);

public static class ReportPalette
{
    public const string Accent = "FFC831";
    public const string Dark = "2B2B2B";

    public static readonly IReadOnlyList<string> Colors =
    [
        "FFC831", "2B2B2B", "4EA8DE", "E07A5F", "81B29A", "B388EB", "9E9E9E",
    ];

    public const int MaxSlices = 6;

    public static IReadOnlyList<ChartSlice> Group(IReadOnlyList<ChartSlice> slices, Func<decimal, string> display)
    {
        var positive = slices.Where(s => s.Value > 0).OrderByDescending(s => s.Value).ToList();
        if (positive.Count <= MaxSlices)
        {
            return positive;
        }

        var top = positive.Take(MaxSlices - 1).ToList();
        var rest = positive.Skip(MaxSlices - 1).Sum(s => s.Value);
        top.Add(new ChartSlice("Otros", rest, display(rest)));
        return top;
    }
}
