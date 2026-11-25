using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace CraneoBarbero.Api.Reports.Export;

public sealed record XlsxBlock(int FirstRow, int LastRow);

public sealed class SimpleXlsx(string sheetName)
{
    private const int TitleStyle = 1;
    private const int SubtitleStyle = 2;
    private const int SectionStyle = 3;
    private const int HeaderStyle = 4;
    private const int TextStyle = 5;
    private const int MoneyStyle = 6;
    private const int IntegerStyle = 7;
    private const int PercentStyle = 8;
    private const int NoteStyle = 9;
    private const int ParagraphStyle = 10;
    private const int ChartColumn = 8;
    private const int ChartRows = 17;
    private const int ChartColumns = 7;

    private const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="2"><numFmt numFmtId="164" formatCode="&quot;$&quot;#,##0.00"/><numFmt numFmtId="165" formatCode="0.00&quot;%&quot;"/></numFmts>
          <fonts count="6">
            <font><sz val="11"/><name val="Calibri"/></font>
            <font><b/><sz val="16"/><name val="Calibri"/></font>
            <font><sz val="11"/><color rgb="FF666666"/><name val="Calibri"/></font>
            <font><b/><sz val="13"/><name val="Calibri"/></font>
            <font><b/><sz val="11"/><name val="Calibri"/></font>
            <font><i/><sz val="10"/><color rgb="FF666666"/><name val="Calibri"/></font>
          </fonts>
          <fills count="3">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFC831"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color rgb="FFD0D0D0"/></left><right style="thin"><color rgb="FFD0D0D0"/></right><top style="thin"><color rgb="FFD0D0D0"/></top><bottom style="thin"><color rgb="FFD0D0D0"/></bottom><diagonal/></border>
          </borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="11">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="4" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="3" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="1" xfId="0" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="0" fontId="5" fillId="0" borderId="0" xfId="0" applyFont="1"/>
            <xf numFmtId="0" fontId="5" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
          </cellXfs>
          <cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
        </styleSheet>
        """;

    private sealed record Cell(int Column, object? Value, int Style);

    private sealed record Row(int Number, List<Cell> Cells, float? Height);

    private sealed record Pie(string Title, XlsxBlock Block, int LabelColumn, int ValueColumn);

    private readonly List<Row> _rows = [];
    private readonly List<Pie> _pies = [];
    private readonly List<string> _merges = [];
    private int _next = 1;

    public void Title(string text) => Add([new Cell(0, text, TitleStyle)], 26f);

    public void Subtitle(string text) => Add([new Cell(0, text, SubtitleStyle)]);

    public void Paragraph(string text, int columns)
    {
        _merges.Add($"A{_next}:{Column(columns - 1)}{_next}");
        Add([new Cell(0, text, ParagraphStyle)], 48f);
    }

    public void Blank() => _next++;

    public XlsxBlock Section(string title, IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows, int? percentColumn = null)
    {
        Add([new Cell(0, title, SectionStyle)], 20f);
        Add(headers.Select((h, i) => new Cell(i, h, HeaderStyle)).ToList());
        var first = _next;
        if (rows.Count == 0)
        {
            Add([new Cell(0, "Sin datos en el período.", NoteStyle)]);
        }

        foreach (var row in rows)
        {
            Add(row.Select((value, i) => new Cell(i, value, value switch
            {
                decimal when i == percentColumn => PercentStyle,
                decimal => MoneyStyle,
                int => IntegerStyle,
                _ => TextStyle,
            })).ToList());
        }

        var block = new XlsxBlock(first, _next - 1);
        Blank();
        return block;
    }

    public void PieChart(string title, XlsxBlock block, int labelColumn, int valueColumn)
    {
        var values = Values(block, valueColumn);
        if (values.Sum() > 0)
        {
            _pies.Add(new Pie(title, block, labelColumn, valueColumn));
        }
    }

    public byte[] Build()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes());
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{SecurityElement.Escape(SheetName)}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/styles.xml", Styles);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet());
            if (_pies.Count > 0)
            {
                Add(zip, "xl/worksheets/_rels/sheet1.xml.rels", """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/>
                    </Relationships>
                    """);
                Add(zip, "xl/drawings/drawing1.xml", Drawing());
                var relationships = string.Concat(_pies.Select((_, i) =>
                    $"""<Relationship Id="rId{i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart" Target="../charts/chart{i + 1}.xml"/>"""));
                Add(zip, "xl/drawings/_rels/drawing1.xml.rels",
                    $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}</Relationships>""");
                for (var i = 0; i < _pies.Count; i++)
                {
                    Add(zip, $"xl/charts/chart{i + 1}.xml", Chart(_pies[i]));
                }
            }
        }

        return output.ToArray();
    }

    private string SheetName => new string(sheetName.Where(c => !"[]:*?/\\'".Contains(c)).ToArray()) is { Length: > 0 } clean
        ? clean[..Math.Min(clean.Length, 31)]
        : "Hoja1";

    private void Add(List<Cell> cells, float? height = null)
    {
        _rows.Add(new Row(_next, cells, height));
        _next++;
    }

    private List<decimal> Values(XlsxBlock block, int column) =>
        _rows.Where(r => r.Number >= block.FirstRow && r.Number <= block.LastRow)
            .Select(r => r.Cells.FirstOrDefault(c => c.Column == column)?.Value switch
            {
                decimal d => d,
                int n => n,
                _ => 0m,
            })
            .ToList();

    private List<string> Labels(XlsxBlock block, int column) =>
        _rows.Where(r => r.Number >= block.FirstRow && r.Number <= block.LastRow)
            .Select(r => Display(r.Cells.FirstOrDefault(c => c.Column == column)?.Value))
            .ToList();

    private string Sheet()
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
        xml.Append("""<sheetViews><sheetView workbookViewId="0" showGridLines="0"/></sheetViews><sheetFormatPr defaultRowHeight="15"/><cols>""");
        var tableCells = _rows.SelectMany(r => r.Cells).Where(c => c.Style is >= HeaderStyle and <= PercentStyle).ToList();
        var columns = tableCells.Count == 0 ? 1 : tableCells.Max(c => c.Column) + 1;
        for (var c = 0; c < columns; c++)
        {
            var longest = tableCells.Where(cell => cell.Column == c).Select(cell => Display(cell.Value).Length + (cell.Style == MoneyStyle ? 4 : 0)).DefaultIfEmpty(0).Max();
            var width = Math.Clamp(longest + 3, c == 0 ? 16 : 11, 45);
            xml.Append(CultureInfo.InvariantCulture, $"""<col min="{c + 1}" max="{c + 1}" width="{width}" customWidth="1"/>""");
        }

        xml.Append("</cols><sheetData>");
        foreach (var row in _rows)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<row r="{row.Number}" """);
            if (row.Height is { } height)
            {
                xml.Append(CultureInfo.InvariantCulture, $"""ht="{height}" customHeight="1" """);
            }

            xml.Append('>');
            foreach (var cell in row.Cells)
            {
                AppendCell(xml, Reference(cell.Column, row.Number), cell);
            }

            xml.Append("</row>");
        }

        xml.Append("</sheetData>");
        if (_merges.Count > 0)
        {
            xml.Append(CultureInfo.InvariantCulture, $"""<mergeCells count="{_merges.Count}">{string.Concat(_merges.Select(m => $"<mergeCell ref=\"{m}\"/>"))}</mergeCells>""");
        }

        xml.Append("""<pageMargins left="0.5" right="0.5" top="0.6" bottom="0.6" header="0.3" footer="0.3"/>""");
        if (_pies.Count > 0)
        {
            xml.Append("""<drawing r:id="rId1"/>""");
        }

        xml.Append("</worksheet>");
        return xml.ToString();
    }

    private static void AppendCell(StringBuilder xml, string reference, Cell cell)
    {
        switch (cell.Value)
        {
            case null:
                xml.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{cell.Style}"/>""");
                break;
            case decimal value:
                xml.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{cell.Style}"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>""");
                break;
            case int value:
                xml.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{cell.Style}"><v>{value}</v></c>""");
                break;
            default:
                xml.Append(CultureInfo.InvariantCulture,
                    $"""<c r="{reference}" s="{cell.Style}" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(Display(cell.Value))}</t></is></c>""");
                break;
        }
    }

    private string Drawing()
    {
        var anchors = new StringBuilder();
        for (var i = 0; i < _pies.Count; i++)
        {
            var fromRow = 1 + i * (ChartRows + 1);
            anchors.Append(CultureInfo.InvariantCulture, $"""
                <xdr:twoCellAnchor editAs="oneCell">
                <xdr:from><xdr:col>{ChartColumn}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{fromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>
                <xdr:to><xdr:col>{ChartColumn + ChartColumns}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>{fromRow + ChartRows}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>
                <xdr:graphicFrame macro=""><xdr:nvGraphicFramePr><xdr:cNvPr id="{i + 2}" name="Gráfico {i + 1}"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>
                <xdr:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></xdr:xfrm>
                <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" r:id="rId{i + 1}"/></a:graphicData></a:graphic>
                </xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor>
                """);
        }

        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">{anchors}</xdr:wsDr>""";
    }

    private string Chart(Pie pie)
    {
        var labels = Labels(pie.Block, pie.LabelColumn);
        var values = Values(pie.Block, pie.ValueColumn);
        var sheet = $"'{SheetName}'";
        var labelRange = $"{sheet}!${Column(pie.LabelColumn)}${pie.Block.FirstRow}:${Column(pie.LabelColumn)}${pie.Block.LastRow}";
        var valueRange = $"{sheet}!${Column(pie.ValueColumn)}${pie.Block.FirstRow}:${Column(pie.ValueColumn)}${pie.Block.LastRow}";
        var title = SecurityElement.Escape(pie.Title);
        var points = string.Concat(Enumerable.Range(0, values.Count).Select(i =>
            $"""<c:dPt><c:idx val="{i}"/><c:bubble3D val="0"/><c:spPr><a:solidFill><a:srgbClr val="{ReportPalette.Colors[i % ReportPalette.Colors.Count]}"/></a:solidFill><a:ln w="12700"><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill></a:ln></c:spPr></c:dPt>"""));
        var labelCache = string.Concat(labels.Select((label, i) => $"""<c:pt idx="{i}"><c:v>{SecurityElement.Escape(label)}</c:v></c:pt>"""));
        var valueCache = string.Concat(values.Select((value, i) => $"""<c:pt idx="{i}"><c:v>{value.ToString(CultureInfo.InvariantCulture)}</c:v></c:pt>"""));
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
            <c:roundedCorners val="0"/>
            <c:chart>
            <c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz="1300" b="1"/></a:pPr><a:r><a:rPr lang="es-AR" sz="1300" b="1"/><a:t>{title}</a:t></a:r></a:p></c:rich></c:tx><c:overlay val="0"/></c:title>
            <c:autoTitleDeleted val="0"/>
            <c:plotArea><c:layout/>
            <c:pieChart><c:varyColors val="1"/>
            <c:ser><c:idx val="0"/><c:order val="0"/><c:tx><c:v>{title}</c:v></c:tx>{points}
            <c:dLbls><c:numFmt formatCode="0.0%;;;" sourceLinked="0"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr><c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz="900" b="1"/></a:pPr><a:endParaRPr lang="es-AR"/></a:p></c:txPr><c:dLblPos val="outEnd"/><c:showLegendKey val="0"/><c:showVal val="0"/><c:showCatName val="0"/><c:showSerName val="0"/><c:showPercent val="1"/><c:showBubbleSize val="0"/><c:showLeaderLines val="1"/></c:dLbls>
            <c:cat><c:strRef><c:f>{SecurityElement.Escape(labelRange)}</c:f><c:strCache><c:ptCount val="{labels.Count}"/>{labelCache}</c:strCache></c:strRef></c:cat>
            <c:val><c:numRef><c:f>{SecurityElement.Escape(valueRange)}</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val="{values.Count}"/>{valueCache}</c:numCache></c:numRef></c:val>
            </c:ser>
            <c:firstSliceAng val="0"/>
            </c:pieChart>
            </c:plotArea>
            <c:legend><c:legendPos val="r"/><c:overlay val="0"/></c:legend>
            <c:plotVisOnly val="1"/>
            <c:dispBlanksAs val="gap"/>
            </c:chart>
            </c:chartSpace>
            """;
    }

    private static string Display(object? value) => value switch
    {
        null => string.Empty,
        decimal d => d.ToString("#,##0.00", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Reference(int column, int row) => Column(column) + row.ToString(CultureInfo.InvariantCulture);

    private static string Column(int index)
    {
        var name = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    private string ContentTypes()
    {
        var charts = string.Concat(_pies.Select((_, i) =>
            $"""<Override PartName="/xl/charts/chart{i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.drawingml.chart+xml"/>"""));
        var drawing = _pies.Count > 0
            ? """<Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>"""
            : string.Empty;
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>{drawing}{charts}</Types>""";
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content.Trim());
        stream.Write(bytes);
    }
}
