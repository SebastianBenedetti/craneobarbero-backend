using System.Globalization;
using System.Text;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record PdfColumn(string Header, float Weight, bool AlignRight = false);

public sealed class SimplePdf
{
    private const float PageWidth = 595f;
    private const float PageHeight = 842f;
    private const float Margin = 40f;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float RowHeight = 15f;
    private const float FontSize = 9f;

    private static readonly int[] HelveticaWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = null!;
    private float _y;

    public SimplePdf(string title, IEnumerable<string> subtitles)
    {
        NewPage();
        Text(Margin, _y, title, 16f, bold: true);
        _y -= 22f;
        foreach (var line in subtitles)
        {
            Text(Margin, _y, line, 10f, bold: false);
            _y -= 14f;
        }

        _y -= 6f;
    }

    public void Heading(string text)
    {
        EnsureSpace(RowHeight * 3);
        _y -= 8f;
        Text(Margin, _y, text, 12f, bold: true);
        _y -= 18f;
    }

    public void Paragraph(string text)
    {
        EnsureSpace(RowHeight);
        Text(Margin, _y, Fit(text, ContentWidth, 10f, false), 10f, bold: false);
        _y -= 14f;
    }

    public void Table(IReadOnlyList<PdfColumn> columns, IReadOnlyList<string[]> rows)
    {
        var total = columns.Sum(c => c.Weight);
        var widths = columns.Select(c => c.Weight / total * ContentWidth).ToArray();
        DrawHeader(columns, widths);
        if (rows.Count == 0)
        {
            Text(Margin + 4f, _y - 10.5f, "Sin datos en el período.", FontSize, bold: false);
            _y -= RowHeight;
            return;
        }

        foreach (var row in rows)
        {
            if (_y - RowHeight < Margin)
            {
                NewPage();
                DrawHeader(columns, widths);
            }

            DrawRow(columns, widths, row, bold: false);
        }
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

    private void DrawHeader(IReadOnlyList<PdfColumn> columns, float[] widths)
    {
        EnsureSpace(RowHeight * 2);
        _page.Append(CultureInfo.InvariantCulture, $"0.92 g {N(Margin)} {N(_y - RowHeight)} {N(ContentWidth)} {N(RowHeight)} re f 0 g\n");
        DrawRow(columns, widths, columns.Select(c => c.Header).ToArray(), bold: true);
    }

    private void DrawRow(IReadOnlyList<PdfColumn> columns, float[] widths, string[] cells, bool bold)
    {
        var x = Margin;
        for (var i = 0; i < columns.Count; i++)
        {
            var available = widths[i] - 8f;
            var text = Fit(i < cells.Length ? cells[i] : string.Empty, available, FontSize, bold);
            var left = columns[i].AlignRight ? x + widths[i] - 4f - Width(text, FontSize, bold) : x + 4f;
            Text(left, _y - 10.5f, text, FontSize, bold);
            x += widths[i];
        }

        _page.Append(CultureInfo.InvariantCulture, $"0.8 G 0.5 w {N(Margin)} {N(_y - RowHeight)} m {N(Margin + ContentWidth)} {N(_y - RowHeight)} l S 0 G\n");
        _y -= RowHeight;
    }

    private void EnsureSpace(float height)
    {
        if (_y - height < Margin)
        {
            NewPage();
        }
    }

    private void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        _y = PageHeight - Margin;
    }

    private void Text(float x, float y, string text, float size, bool bold) =>
        _page.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(y)} Td ({Escape(text)}) Tj ET\n");

    private static string Footer(int page, int count) =>
        $"BT /F1 8 Tf {N(PageWidth - Margin - 60f)} {N(Margin / 2)} Td (Página {page} de {count}) Tj ET\n";

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
        _ => c,
    };

    private static string Clean(string text) =>
        new(text.Replace('—', '-').Replace('–', '-').Select(c => c <= 'ÿ' && !char.IsControl(c) ? c : '?').ToArray());

    private static string Escape(string text) =>
        Clean(text).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
