using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CraneoBarbero.Api.Data.Import;

public sealed class DumpTable(string name, IReadOnlyList<string> columns)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Columns { get; } = columns;
    public List<DumpRow> Rows { get; } = [];
}

public sealed class DumpRow(IReadOnlyDictionary<string, string?> values)
{
    public string? this[string column] =>
        values.TryGetValue(column, out var value)
            ? value
            : throw new InvalidOperationException($"El respaldo no tiene la columna '{column}'.");

    public string Required(string column) =>
        this[column] ?? throw new InvalidOperationException($"La columna '{column}' viene vacía en el respaldo.");
}

public static partial class PostgresDump
{
    public static async Task<IReadOnlyDictionary<string, DumpTable>> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;
        await using (stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadPublicTablesAsync(reader, cancellationToken);
        }
    }

    public static async Task<IReadOnlyDictionary<string, DumpTable>> ReadPublicTablesAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, DumpTable>(StringComparer.Ordinal);
        DumpTable? current = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (current is null)
            {
                var match = CopyHeader().Match(line);
                if (match.Success)
                {
                    var columns = match.Groups["columns"].Value
                        .Split(',', StringSplitOptions.TrimEntries)
                        .Select(column => column.Trim('"'))
                        .ToArray();
                    current = new DumpTable(match.Groups["table"].Value.Trim('"'), columns);
                }

                continue;
            }

            if (line == "\\.")
            {
                tables[current.Name] = current;
                current = null;
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != current.Columns.Count)
            {
                throw new InvalidOperationException($"Una fila de '{current.Name}' no tiene la cantidad de columnas esperada.");
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Length; i++)
            {
                values[current.Columns[i]] = Unescape(fields[i]);
            }

            current.Rows.Add(new DumpRow(values));
        }

        return tables;
    }

    public static string? Unescape(string field)
    {
        if (field == "\\N")
        {
            return null;
        }

        if (!field.Contains('\\'))
        {
            return field;
        }

        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            var character = field[i];
            if (character != '\\' || i == field.Length - 1)
            {
                builder.Append(character);
                continue;
            }

            var next = field[++i];
            builder.Append(next switch
            {
                't' => '\t',
                'n' => '\n',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => next,
            });
        }

        return builder.ToString();
    }

    [GeneratedRegex("""^COPY public\.(?<table>"?[A-Za-z_]+"?) \((?<columns>[^)]*)\) FROM stdin;$""")]
    private static partial Regex CopyHeader();
}
