using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>How a virtual user picks the next value from a <see cref="ParameterList"/>.</summary>
public enum ParameterSelection
{
    /// <summary>Values are handed out in order from a counter shared by all virtual users, wrapping at the end.</summary>
    Sequential,

    /// <summary>Each iteration picks a value at random.</summary>
    Random,
}

/// <summary>
/// A named list of values, such as user ids, that requests reference as <c>{{name}}</c> in a URL, header value or
/// body. Every iteration of a virtual user takes one value from each list, so all requests in that iteration see the
/// same value.
/// </summary>
public sealed partial class ParameterList
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The values to substitute. Hand-written files may list numbers or booleans; they are read as text.</summary>
    [JsonConverter(typeof(ScalarStringListConverter))]
    public List<string> Values { get; set; } = [];

    public ParameterSelection Selection { get; set; } = ParameterSelection.Sequential;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    internal static partial Regex NamePattern();

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Every parameter list needs a name.";
        }
        else if (!NamePattern().IsMatch(Name))
        {
            yield return $"Parameter list name '{Name}' can only use letters, digits and underscores, and can't start with a digit.";
        }

        if (Values.Count == 0)
        {
            yield return $"Parameter list '{Name}' needs at least one value.";
        }
    }
}

/// <summary>
/// The parameter lists and data files of a plan, prepared for lookups while a test runs. One instance is shared by all
/// virtual users, so sequential selection hands out values in order across all of them. Every list and every column of
/// a data file has a slot in the row an iteration works with; the columns of a data file are filled from one row of the
/// file, so values that belong together stay together.
/// </summary>
public sealed class ParameterSet
{
    private readonly ParameterList[] _lists;
    private readonly (DataTable Table, ParameterSelection Selection, int FirstSlot)[] _tables;
    private readonly Dictionary<string, int> _indexByName;
    private readonly long[] _counters;

    public ParameterSet(IEnumerable<ParameterList> lists, IEnumerable<DataFile>? dataFiles = null)
    {
        _lists = [.. lists];
        _indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _lists.Length; i++)
        {
            if (_lists[i].Name is { } name)
            {
                _indexByName.TryAdd(name, i);
            }
        }

        // A file that can't be read is left out; the plan reports it when it is validated.
        var slot = _lists.Length;
        var tables = new List<(DataTable, ParameterSelection, int)>();
        foreach (var file in dataFiles ?? [])
        {
            if (!file.TryGetTable(out var table, out _) || table.Rows.Count == 0)
            {
                continue;
            }

            tables.Add((table, file.Selection, slot));
            foreach (var column in table.Columns)
            {
                _indexByName.TryAdd(column, slot++);
            }
        }

        _tables = [.. tables];
        Count = slot;
        _counters = new long[_lists.Length + _tables.Length];
    }

    /// <summary>The size of the row <see cref="Next"/> fills: one value per list and per data column.</summary>
    public int Count { get; }

    /// <summary>The names of the variables the lists and data columns supply.</summary>
    public IEnumerable<string> Names => _indexByName.Keys;

    internal bool TryGetIndex(string name, out int index) => _indexByName.TryGetValue(name, out index);

    /// <summary>Fills <paramref name="row"/> with the next value of every list and the next row of every data file, for one iteration.</summary>
    public void Next(string[] row)
    {
        for (var i = 0; i < _lists.Length; i++)
        {
            var values = _lists[i].Values;
            var index = _lists[i].Selection == ParameterSelection.Random
                ? System.Random.Shared.Next(values.Count)
                : (int)((Interlocked.Increment(ref _counters[i]) - 1) % values.Count);
            row[i] = values[index];
        }

        for (var t = 0; t < _tables.Length; t++)
        {
            var (table, selection, firstSlot) = _tables[t];
            var index = selection == ParameterSelection.Random
                ? System.Random.Shared.Next(table.Rows.Count)
                : (int)((Interlocked.Increment(ref _counters[_lists.Length + t]) - 1) % table.Rows.Count);
            table.Rows[index].CopyTo(row, firstSlot);
        }
    }

    /// <summary>The first value of every list and the first row of every data file, used for validation and for sending a single request.</summary>
    public ParameterValues First()
    {
        var row = new string[Count];
        for (var i = 0; i < _lists.Length; i++)
        {
            row[i] = _lists[i].Values.Count > 0 ? _lists[i].Values[0] : string.Empty;
        }

        foreach (var (table, _, firstSlot) in _tables)
        {
            table.Rows[0].CopyTo(row, firstSlot);
        }

        return new ParameterValues(this, row);
    }
}

/// <summary>The values one iteration uses, one per list of a <see cref="ParameterSet"/>.</summary>
public readonly record struct ParameterValues(ParameterSet? Set, string[]? Row)
{
    public bool TryGet(string name, out string value)
    {
        if (Set is not null && Row is not null && Set.TryGetIndex(name, out var index))
        {
            value = Row[index];
            return true;
        }

        value = string.Empty;
        return false;
    }
}

/// <summary>Reads a JSON array of strings, numbers or booleans as text, so <c>[17, 42]</c> works as well as <c>["17", "42"]</c>.</summary>
internal sealed class ScalarStringListConverter : JsonConverter<List<string>>
{
    public override bool HandleNull => true;

    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Parameter values must be a list.");
        }

        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString()!,
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => string.Empty,
                _ => throw new JsonException("Parameter values must be text, numbers or booleans."),
            });
        }

        return values;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
