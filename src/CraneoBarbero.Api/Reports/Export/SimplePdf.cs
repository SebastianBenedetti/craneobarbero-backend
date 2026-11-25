using System.Globalization;
using System.Text;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record PdfColumn(string Header, float Weight, bool AlignRight = false);

public sealed record PdfStat(string Label, string Value, string? Hint = null);

public sealed class SimplePdf
{
    private const float PageWidth = 595f;
    private const float PageHeight = 842f;
    private const float Margin = 42f;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float Bottom = 52f;
    private const float RowHeight = 17f;
    private const float FontSize = 9f;
    private const float BandHeight = 74f;
    private const float PieRadius = 46f;
    private const float LegendEntryHeight = 21f;

    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private const string Black = "0 0 0";
    private const string Gray = "0.42 0.42 0.42";
    private const string White = "1 1 1";

    private readonly List<StringBuilder> _pages = [];
    private readonly string _title;
    private readonly string _footer;
    private StringBuilder _page = null!;
    private float _y;

    public SimplePdf(string title, IReadOnlyList<string> subtitles, string footer)
    {
        _title = title;
        _footer = footer;
        NewPage();
        Rect(0, PageHeight - BandHeight, PageWidth, BandHeight, Rgb(ReportPalette.Dark));
        Rect(0, PageHeight - BandHeight - 4f, PageWidth, 4f, Rgb(ReportPalette.Accent));
        Text(Margin, PageHeight - 36f, title, 19f, bold: true, White);
        Text(Margin, PageHeight - 56f, string.Join("     |     ", subtitles), 9.5f, bold: false, "0.85 0.85 0.85");
        _y = PageHeight - BandHeight - 26f;
    }

    public void Stats(IReadOnlyList<PdfStat> stats)
    {
        const float gap = 10f;
        const float height = 58f;
        EnsureSpace(height + 10f);
        var width = (ContentWidth - gap * (stats.Count - 1)) / stats.Count;
        for (var i = 0; i < stats.Count; i++)
        {
            var x = Margin + i * (width + gap);
            var top = _y;
            Rect(x, top - height, width, height, "0.965 0.965 0.965");
            Rect(x, top - height, 3f, height, Rgb(ReportPalette.Accent));
            Text(x + 11f, top - 15f, Fit(stats[i].Label, width - 16f, 8f, false), 8f, bold: false, Gray);
            Text(x + 11f, top - 34f, Fit(stats[i].Value, width - 16f, 14f, true), 14f, bold: true, Black);
            if (stats[i].Hint is { } hint)
            {
                Text(x + 11f, top - 48f, Fit(hint, width - 16f, 7f, false), 7f, bold: false, Gray);
            }
        }

        _y -= height + 8f;
    }

    public void Section(string title, IReadOnlyList<PdfColumn> columns, IReadOnlyList<string[]> rows)
    {
        Heading(title, RowHeight * (1 + Math.Clamp(rows.Count, 1, 12)));
        Table(columns, rows);
    }

    public void Heading(string text, float following = RowHeight * 3)
    {
        EnsureSpace(46f + following);
        _y -= 16f;
        Text(Margin, _y - 12f, text, 12.5f, bold: true, Black);
        Rect(Margin, _y - 19f, 28f, 2f, Rgb(ReportPalette.Accent));
        _y -= 28f;
    }

    public void Paragraph(string text)
    {
        const float size = 8f;
        const float lineHeight = 11f;
        var lines = Wrap(text, ContentWidth, size);
        EnsureSpace(lines.Count * lineHeight + 14f);
        _y -= 14f;
        foreach (var line in lines)
        {
            Text(Margin, _y - 8f, line, size, bold: false, Gray);
            _y -= lineHeight;
        }
    }

    public void Table(IReadOnlyList<PdfColumn> columns, IReadOnlyList<string[]> rows)
    {
        var total = columns.Sum(c => c.Weight);
        var widths = columns.Select(c => c.Weight / total * ContentWidth).ToArray();
        DrawHeader(columns, widths);
        if (rows.Count == 0)
        {
            Text(Margin + 6f, _y - 11.8f, "Sin datos en el período.", FontSize, bold: false, Gray);
            _y -= RowHeight;
            return;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            if (_y - RowHeight < Bottom)
            {
                NewPage();
                DrawHeader(columns, widths);
            }

            if (r % 2 == 1)
            {
                Rect(Margin, _y - RowHeight, ContentWidth, RowHeight, "0.965 0.965 0.965");
            }

            DrawCells(columns, widths, rows[r], bold: false, Black);
            _y -= RowHeight;
        }

        Line(Margin, _y, Margin + ContentWidth, _y, "0.8 0.8 0.8");
    }

    public void PieRow(IReadOnlyList<ChartData> charts)
    {
        const float gap = 20f;
        var legendHeight = charts.Max(c => c.Slices.Count) * LegendEntryHeight;
        var height = 26f + Math.Max(PieRadius * 2, legendHeight) + 8f;
        EnsureSpace(height + 16f);
        _y -= 16f;
        var width = (ContentWidth - gap * (charts.Count - 1)) / charts.Count;
        for (var i = 0; i < charts.Count; i++)
        {
            DrawPie(charts[i], Margin + i * (width + gap), _y, width, height);
        }

        _y -= height;
    }

    public byte[] Build()
    {
        var latin = Encoding.Latin1;
        using var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string text) => output.Write(latin.GetBytes(text));

        void WriteObject(int id, string body)
        {
            offsets.Add(output.Position);
            Write($"{id} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n%âãÏÓ\n");
        var pageCount = _pages.Count;
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{5 + i * 2} 0 R"));
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        WriteObject(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        WriteObject(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pageCount; i++)
        {
            var content = _pages[i].ToString() + Footer(i + 1, pageCount);
            var bytes = latin.GetByteCount(content);
            WriteObject(5 + i * 2,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + i * 2} 0 R >>");
            WriteObject(6 + i * 2, $"<< /Length {bytes} >>\nstream\n{content}\nendstream");
        }

        var xref = output.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private void DrawPie(ChartData chart, float x, float top, float width, float height)
    {
        Text(x, top - 11f, Fit(chart.Title, width, 10.5f, true), 10.5f, bold: true, Black);
        var areaTop = top - 26f;
        var areaHeight = height - 34f;
        var centerX = x + PieRadius + 2f;
        var centerY = areaTop - areaHeight / 2;
        var total = chart.Slices.Sum(s => s.Value);
        if (total <= 0)
        {
            Circle(centerX, centerY, PieRadius, "0.9 0.9 0.9");
            Text(x + PieRadius * 2 + 18f, centerY - 3f, "Sin datos en el período.", 8.5f, bold: false, Gray);
            return;
        }

        var start = Math.PI / 2;
        for (var i = 0; i < chart.Slices.Count; i++)
        {
            var sweep = (double)(chart.Slices[i].Value / total) * 2 * Math.PI;
            Slice(centerX, centerY, PieRadius, start, start - sweep, Rgb(Color(i)), stroke: chart.Slices.Count > 1);
            start -= sweep;
        }

        var legendX = x + PieRadius * 2 + 18f;
        var legendWidth = x + width - legendX;
        var legendY = centerY + chart.Slices.Count * LegendEntryHeight / 2;
        for (var i = 0; i < chart.Slices.Count; i++)
        {
            var slice = chart.Slices[i];
            var entryTop = legendY - i * LegendEntryHeight;
            var percent = $"{(slice.Value / total * 100).ToString("0.#", CultureInfo.GetCultureInfo("es-AR"))}%";
            var percentWidth = Width(percent, 8f, true);
            Rect(legendX, entryTop - 9f, 7f, 7f, Rgb(Color(i)));
            Text(legendX + 12f, entryTop - 8.5f, Fit(slice.Label, legendWidth - percentWidth - 18f, 8f, false), 8f, bold: false, Black);
            Text(x + width - percentWidth, entryTop - 8.5f, percent, 8f, bold: true, Black);
            Text(legendX + 12f, entryTop - 17.5f, Fit(slice.Display, legendWidth - 12f, 7f, false), 7f, bold: false, Gray);
        }
    }

    private void Slice(float cx, float cy, float r, double from, double to, string color, bool stroke)
    {
        var path = new StringBuilder();
        path.Append(CultureInfo.InvariantCulture, $"{N(cx)} {N(cy)} m {N(cx + r * (float)Math.Cos(from))} {N(cy + r * (float)Math.Sin(from))} l ");
        var segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(to - from) / (Math.PI / 2)));
        var step = (to - from) / segments;
        for (var s = 0; s < segments; s++)
        {
            var a1 = from + s * step;
            var a2 = a1 + step;
            var k = 4.0 / 3.0 * Math.Tan(step / 4);
            var x1 = cx + r * (Math.Cos(a1) - k * Math.Sin(a1));
            var y1 = cy + r * (Math.Sin(a1) + k * Math.Cos(a1));
            var x2 = cx + r * (Math.Cos(a2) + k * Math.Sin(a2));
            var y2 = cy + r * (Math.Sin(a2) - k * Math.Cos(a2));
            path.Append(CultureInfo.InvariantCulture,
                $"{N((float)x1)} {N((float)y1)} {N((float)x2)} {N((float)y2)} {N(cx + r * (float)Math.Cos(a2))} {N(cy + r * (float)Math.Sin(a2))} c ");
        }

        _page.Append(CultureInfo.InvariantCulture, $"{color} rg 1 1 1 RG 1.2 w {path}h {(stroke ? "B" : "f")}\n");
    }

    private void Circle(float cx, float cy, float r, string color) => Slice(cx, cy, r, 0, 2 * Math.PI, color, stroke: false);

    private void DrawHeader(IReadOnlyList<PdfColumn> columns, float[] widths)
    {
        EnsureSpace(RowHeight * 2);
        Rect(Margin, _y - RowHeight, ContentWidth, RowHeight, Rgb(ReportPalette.Dark));
        DrawCells(columns, widths, columns.Select(c => c.Header).ToArray(), bold: true, White);
        _y -= RowHeight;
    }

    private void DrawCells(IReadOnlyList<PdfColumn> columns, float[] widths, string[] cells, bool bold, string color)
    {
        var x = Margin;
        for (var i = 0; i < columns.Count; i++)
        {
            var available = widths[i] - 12f;
            var text = Fit(i < cells.Length ? cells[i] : string.Empty, available, FontSize, bold);
            var left = columns[i].AlignRight ? x + widths[i] - 6f - Width(text, FontSize, bold) : x + 6f;
            Text(left, _y - 11.8f, text, FontSize, bold, color);
            x += widths[i];
        }
    }

    private void EnsureSpace(float height)
    {
        if (_y - height < Bottom)
        {
            NewPage();
        }
    }

    private void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        _y = PageHeight - Margin;
        if (_pages.Count > 1)
        {
            Text(Margin, _y - 8f, _title, 8f, bold: true, Gray);
            Line(Margin, _y - 14f, Margin + ContentWidth, _y - 14f, Rgb(ReportPalette.Accent));
            _y -= 22f;
        }
    }

    private void Rect(float x, float y, float width, float height, string color) =>
        _page.Append(CultureInfo.InvariantCulture, $"{color} rg {N(x)} {N(y)} {N(width)} {N(height)} re f\n");

    private void Line(float x1, float y1, float x2, float y2, string color) =>
        _page.Append(CultureInfo.InvariantCulture, $"{color} RG 0.6 w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S\n");

    private void Text(float x, float y, string text, float size, bool bold, string color) =>
        _page.Append(CultureInfo.InvariantCulture, $"{color} rg BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(y)} Td ({Escape(text)}) Tj ET\n");

    private string Footer(int page, int count)
    {
        var number = $"Página {page} de {count}";
        return $"{Gray} RG 0.5 w {N(Margin)} {N(36f)} m {N(Margin + ContentWidth)} {N(36f)} l S\n" +
            $"{Gray} rg BT /F1 7.5 Tf {N(Margin)} {N(24f)} Td ({Escape(Fit(_footer, ContentWidth - 90f, 7.5f, false))}) Tj ET\n" +
            $"BT /F1 7.5 Tf {N(PageWidth - Margin - Width(number, 7.5f, false))} {N(24f)} Td ({Escape(number)}) Tj ET\n";
    }

    private static string Color(int index) => ReportPalette.Colors[index % ReportPalette.Colors.Count];

    private static string Rgb(string hex)
    {
        float Part(int start) => int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f;
        return $"{Part(0).ToString("0.###", CultureInfo.InvariantCulture)} {Part(2).ToString("0.###", CultureInfo.InvariantCulture)} {Part(4).ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static List<string> Wrap(string text, float available, float size)
    {
        var lines = new List<string>();
        var current = string.Empty;
        foreach (var word in Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && Width(candidate, size, false) > available)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }

    private static string Fit(string text, float available, float size, bool bold)
    {
        text = Clean(text);
        if (Width(text, size, bold) <= available)
        {
            return text;
        }

        while (text.Length > 1 && Width(text + "...", size, bold) > available)
        {
            text = text[..^1];
        }

        return text.TrimEnd() + "...";
    }

    private static float Width(string text, float size, bool bold)
    {
        var units = 0f;
        foreach (var raw in text)
        {
            var c = Base(raw);
            units += c is >= ' ' and <= '~' ? HelveticaWidths[c - ' '] : 556;
        }

        return units / 1000f * size * (bold ? 1.06f : 1f);
    }

    private static char Base(char c) => c switch
    {
        'á' or 'à' or 'ä' or 'â' => 'a',
        'é' or 'è' or 'ë' or 'ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' => 'o',
        'ú' or 'ù' or 'ü' or 'û' => 'u',
        'ñ' => 'n',
        'Á' => 'A',
        'É' => 'E',
        'Í' => 'I',
        'Ó' => 'O',
        'Ú' or 'Ü' => 'U',
        'Ñ' => 'N',
        '·' => '.',
        _ => c,
    };

    private static string Clean(string text) =>
        new(text.Replace('—', '-').Replace('–', '-').Select(c => c <= 'ÿ' && !char.IsControl(c) ? c : '?').ToArray());

    private static string Escape(string text) =>
        Clean(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
