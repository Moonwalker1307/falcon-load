using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>The format of the text in a <see cref="DataFile"/>.</summary>
public enum DataFormat
{
    /// <summary>Comma, semicolon or tab separated values with a header line naming the columns.</summary>
    Csv,

    /// <summary>A JSON array of objects, one per row; the keys name the columns.</summary>
    Json,
}

/// <summary>
/// A table of test data, such as a list of test users, kept as the text of a CSV or JSON file. Every column becomes a
/// variable that requests reference as <c>{{column}}</c>, and every iteration of a virtual user takes one whole row, so
/// an email and a password from the same row are always used together. The text is stored with the test, which is
/// what lets an exported test run on the command line or on an agent without the original file.
/// </summary>
public sealed partial class DataFile
{
    /// <summary>
    /// The most text a data file may hold. The text is saved with the test, sent to agents and held in memory while a
    /// test runs, so it can't grow without bound.
    /// </summary>
    public const int MaxContentLength = 5_000_000;

    private volatile ParsedData? _parsed;

    /// <summary>What people call the file, usually its file name, such as <c>users.csv</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public DataFormat Format { get; set; } = DataFormat.Csv;

    /// <summary>The text of the file.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Whether rows are handed out in order (shared by all virtual users, starting over at the end) or at random.</summary>
    public ParameterSelection Selection { get; set; } = ParameterSelection.Sequential;

    /// <summary>
    /// The number of rows the file had when its text was left out of a run's result (see
    /// <see cref="LoadTestPlan.DropDataRows"/>); 0 for a file that still has its text. A result keeps this count
    /// instead of the rows, which can be large and can hold personal data.
    /// </summary>
    public int OmittedRows { get; set; }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ColumnPattern();

    /// <summary>Guesses the format from a file name, falling back to what the text looks like.</summary>
    public static DataFormat DetectFormat(string? fileName, string content)
    {
        if (fileName is not null)
        {
            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return DataFormat.Json;
            }

            if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            {
                return DataFormat.Csv;
            }
        }

        var first = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        return first.StartsWith('[') || first.StartsWith('{') ? DataFormat.Json : DataFormat.Csv;
    }

    /// <summary>
    /// The rows of the file. The result is kept until the text or format changes, so checking a test again, as the
    /// editor does on every change, doesn't read a large file again.
    /// </summary>
    public bool TryGetTable(out DataTable table, out string error)
    {
        var parsed = _parsed;
        if (parsed is null || !ReferenceEquals(parsed.Content, Content) || parsed.Format != Format)
        {
            parsed = Parse(Content, Format);
            _parsed = parsed;
        }

        table = parsed.Table ?? DataTable.Empty;
        error = parsed.Error ?? string.Empty;
        return parsed.Table is not null;
    }

    /// <summary>Returns human readable problems; empty when the file can be saved and used.</summary>
    public IEnumerable<string> Validate()
    {
        var label = string.IsNullOrWhiteSpace(Name) ? "data file" : $"data file '{Name}'";
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Every data file needs a name.";
        }

        if (Content.Length == 0 && OmittedRows > 0)
        {
            yield return $"The rows of {label} weren't kept with this result. Load them again from the saved test.";
            yield break;
        }

        if (Content.Length > MaxContentLength)
        {
            yield return $"The {label} is larger than {MaxContentLength / 1_000_000} MB. Use a shorter file; the data is saved with the test and sent to every agent.";
            yield break;
        }

        if (!TryGetTable(out var table, out var error))
        {
            yield return $"The {label} can't be read: {error}";
            yield break;
        }

        if (table.Rows.Count == 0)
        {
            yield return Format == DataFormat.Csv
                ? $"The {label} has a header line but no rows."
                : $"The {label} has no rows.";
        }

        if (table.Columns.Count == 0)
        {
            // Only JSON can get here, as [{}]: a CSV header always names at least one column.
            yield return $"The {label} has no columns: its objects have no keys.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            if (column.Length == 0)
            {
                yield return $"Column {i + 1} of the {label} has no name.";
            }
            else if (!ColumnPattern().IsMatch(column))
            {
                yield return $"Column '{column}' of the {label} can't be used as a variable name: use letters, digits and underscores, and don't start with a digit.";
            }
            else if (!seen.Add(column))
            {
                yield return $"The {label} has more than one column named '{column}'.";
            }
        }
    }

    /// <summary>
    /// A copy with every <paramref name="count"/>th row starting at <paramref name="index"/>, used to deal rows out to
    /// agents. The copy is CSV whatever the original format was.
    /// </summary>
    public DataFile TakeEvery(int count, int index)
    {
        TryGetTable(out var table, out _);
        return new DataFile
        {
            Name = Name,
            Format = DataFormat.Csv,
            Selection = Selection,
            Content = table.WithRows(table.Rows.Where((_, k) => k % count == index)).ToCsv(),
        };
    }

    /// <summary>A copy without the text, remembering how many rows there were.</summary>
    public DataFile WithoutRows()
    {
        var rows = OmittedRows;
        if (Content.Length > 0 && TryGetTable(out var table, out _))
        {
            rows = table.Rows.Count;
        }

        return new DataFile { Name = Name, Format = Format, Selection = Selection, OmittedRows = rows };
    }

    private static ParsedData Parse(string content, DataFormat format)
    {
        try
        {
            var table = format == DataFormat.Json ? DataTable.ParseJson(content) : DataTable.ParseCsv(content);
            return new ParsedData(content, format, table, null);
        }
        catch (DataFormatException ex)
        {
            return new ParsedData(content, format, null, ex.Message);
        }
    }

    private sealed record ParsedData(string Content, DataFormat Format, DataTable? Table, string? Error);
}

/// <summary>The rows and columns read from a <see cref="DataFile"/>. Every value is text.</summary>
public sealed class DataTable
{
    public static readonly DataTable Empty = new([], []);

    public DataTable(IReadOnlyList<string> columns, IReadOnlyList<string[]> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }

    /// <summary>Each row has one value per column.</summary>
    public IReadOnlyList<string[]> Rows { get; }

    public DataTable WithRows(IEnumerable<string[]> rows) => new(Columns, [.. rows]);

    /// <summary>
    /// Reads comma, semicolon or tab separated text (whichever the header line uses most) with a header line. Values
    /// may be quoted, with <c>""</c> for a quote and line breaks allowed inside the quotes. Blank lines are skipped.
    /// </summary>
    public static DataTable ParseCsv(string text)
    {
        var start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        var delimiter = DetectDelimiter(text, start);
        var records = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var afterQuote = false;
        var sawQuote = false;
        var line = 1;
        var recordLine = 1;
        var quoteLine = 0;
        var width = -1;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            afterQuote = false;
        }

        void EndRecord()
        {
            // A line with nothing on it is skipped, but "" is a real (empty) value.
            if (fields.Count == 0 && field.Length == 0 && !sawQuote)
            {
                return;
            }

            EndField();
            if (width < 0)
            {
                width = fields.Count;
            }
            else if (fields.Count != width)
            {
                throw new DataFormatException($"line {recordLine} has {fields.Count} {(fields.Count == 1 ? "value" : "values")} but the header has {width} {(width == 1 ? "column" : "columns")}.");
            }

            records.Add([.. fields]);
            fields.Clear();
            sawQuote = false;
        }

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            if (afterQuote && c != delimiter && c != '\n' && c != '\r')
            {
                throw new DataFormatException($"line {line} has text after a closing quote.");
            }

            if (c == '"' && field.Length == 0 && !afterQuote)
            {
                inQuotes = true;
                sawQuote = true;
                quoteLine = line;
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRecord();
                line++;
                recordLine = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new DataFormatException($"the quote that opens on line {quoteLine} is never closed.");
        }

        EndRecord();
        if (records.Count == 0)
        {
            throw new DataFormatException("the file is empty. The first line has to name the columns.");
        }

        var columns = records[0].Select(c => c.Trim()).ToArray();
        return new DataTable(columns, records.Skip(1).ToArray());
    }

    /// <summary>
    /// Reads a JSON array of objects. Columns are the keys, in the order they first appear; a row without a key has an
    /// empty value there. Numbers and booleans become their text, <c>null</c> becomes empty, and a nested object or
    /// array becomes its JSON text.
    /// </summary>
    public static DataTable ParseJson(string text)
    {
        try
        {
            return ParseJsonCore(text);
        }
        catch (InvalidOperationException ex) when (JsonErrors.IsInvalidJson(ex))
        {
            throw new DataFormatException($"not valid JSON ({ex.Message.TrimEnd('.')}).");
        }
    }

    private static DataTable ParseJsonCore(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new DataFormatException($"not valid JSON ({ex.Message.TrimEnd('.')}).");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new DataFormatException("the JSON has to be an array of objects, such as [{\"email\": \"a@example.com\"}].");
            }

            var columns = new List<string>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var objects = new List<List<KeyValuePair<int, string>>>();
            var number = 0;
            foreach (var item in root.EnumerateArray())
            {
                number++;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new DataFormatException($"item {number} of the array isn't an object. Every row has to be an object with the same keys; use a parameter list for a plain list of values.");
                }

                var values = new List<KeyValuePair<int, string>>();
                foreach (var property in item.EnumerateObject())
                {
                    if (!index.TryGetValue(property.Name, out var column))
                    {
                        column = columns.Count;
                        columns.Add(property.Name);
                        index[property.Name] = column;
                    }

                    values.Add(new(column, property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString()!,
                        JsonValueKind.Null => string.Empty,
                        _ => property.Value.GetRawText(),
                    }));
                }

                objects.Add(values);
            }

            var rows = new List<string[]>(objects.Count);
            foreach (var values in objects)
            {
                var row = new string[columns.Count];
                Array.Fill(row, string.Empty);
                foreach (var (column, value) in values)
                {
                    row[column] = value;
                }

                rows.Add(row);
            }

            return new DataTable(columns, rows);
        }
    }

    /// <summary>The table as comma separated text that <see cref="ParseCsv"/> reads back as the same rows.</summary>
    public string ToCsv()
    {
        var builder = new StringBuilder();
        AppendRecord(builder, Columns, single: false);
        foreach (var row in Rows)
        {
            AppendRecord(builder, row, single: Columns.Count == 1);
        }

        return builder.ToString();
    }

    private static void AppendRecord(StringBuilder builder, IReadOnlyList<string> values, bool single)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var value = values[i];
            // An empty value alone on a line would read back as a blank line, so it is quoted.
            if (value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || (single && value.Length == 0))
            {
                builder.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        builder.Append('\n');
    }

    private static char DetectDelimiter(string text, int start)
    {
        int commas = 0, semicolons = 0, tabs = 0;
        var inQuotes = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c is '\n' or '\r')
                {
                    break;
                }

                switch (c)
                {
                    case ',':
                        commas++;
                        break;
                    case ';':
                        semicolons++;
                        break;
                    case '\t':
                        tabs++;
                        break;
                }
            }
        }

        if (tabs > commas && tabs >= semicolons)
        {
            return '\t';
        }

        return semicolons > commas ? ';' : ',';
    }
}

/// <summary>The text of a data file can't be read as a table.</summary>
internal sealed class DataFormatException(string message) : Exception(message);
