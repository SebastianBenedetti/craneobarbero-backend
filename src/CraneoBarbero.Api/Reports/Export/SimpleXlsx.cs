using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record XlsxSheet(string Name, IReadOnlyList<string> Headers, IReadOnlyList<object?[]> Rows);

public static class SimpleXlsx
{
    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00"/></numFmts>
          <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="4">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="1" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    public static byte[] Build(IReadOnlyList<XlsxSheet> sheets)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var names = UniqueNames(sheets.Select(s => s.Name).ToList());
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml", Workbook(names));
            Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRelationships(sheets.Count));
            Add(zip, "xl/styles.xml", Styles);
            for (var i = 0; i < sheets.Count; i++)
            {
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i]));
            }
        }

        return output.ToArray();
    }

    private static string Sheet(XlsxSheet sheet)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Append("<cols>");
        for (var c = 0; c < sheet.Headers.Count; c++)
        {
            var width = Math.Clamp(Math.Max(sheet.Headers[c].Length, sheet.Rows.Select(r => c < r.Length ? Display(r[c]).Length : 0).DefaultIfEmpty(0).Max()) + 2, 8, 60);
            xml.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{width}" customWidth="1"/>""");
        }

        xml.Append("</cols><sheetData>");
        AppendRow(xml, 1, sheet.Headers.Cast<object?>().ToArray(), header: true);
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            AppendRow(xml, r + 2, sheet.Rows[r], header: false);
        }

        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    private static void AppendRow(StringBuilder xml, int number, object?[] cells, bool header)
    {
        xml.Append(CultureInfo.InvariantCulture, $"""<row r="{number}">""");
        for (var c = 0; c < cells.Length; c++)
        {
            var reference = Column(c) + number.ToString(CultureInfo.InvariantCulture);
            switch (cells[c])
            {
                case null:
                    break;
                case decimal value when !header:
                    xml.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="2"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>""");
                    break;
                case int value when !header:
                    xml.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="3"><v>{value}</v></c>""");
                    break;
                default:
                    xml.Append(CultureInfo.InvariantCulture,
                        $"""<c r="{reference}" t="inlineStr"{(header ? " s=\"1\"" : "")}><is><t xml:space="preserve">{SecurityElement.Escape(Display(cells[c]))}</t></is></c>""");
                    break;
            }
        }

        xml.Append("</row>");
    }

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    private static List<string> UniqueNames(List<string> names)
    {
        var result = new List<string>();
        foreach (var raw in names)
        {
            var clean = new string(raw.Where(c => !"[]:*?/\\".Contains(c)).ToArray()).Trim();
            clean = clean.Length == 0 ? "Hoja" : clean[..Math.Min(clean.Length, 28)];
            var candidate = clean;
            for (var i = 2; result.Contains(candidate, StringComparer.OrdinalIgnoreCase); i++)
            {
                candidate = $"{clean} {i}";
            }

            result.Add(candidate);
        }

        return result;
    }

    private static string ContentTypes(int sheets)
    {
        var overrides = string.Concat(Enumerable.Range(1, sheets).Select(i =>
            $"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>{overrides}</Types>""";
    }

    private static string Workbook(List<string> names)
    {
        var sheets = string.Concat(names.Select((name, i) =>
            $"""<sheet name="{SecurityElement.Escape(name)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>{sheets}</sheets></workbook>""";
    }

    private static string WorkbookRelationships(int sheets)
    {
        var relationships = string.Concat(Enumerable.Range(1, sheets).Select(i =>
            $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>"""));
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}<Relationship Id="rId{sheets + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""";
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content.Trim());
        stream.Write(bytes);
    }
}
