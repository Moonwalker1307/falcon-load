using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace FalconLoad.Core.Model;

/// <summary>
/// How falcon-load reads JSON that people and other tools write: plan and environment files, results, settings and
/// API bodies. An explicit null where the model has no room for one is treated as left out, so the code behind it never
/// meets a null it doesn't expect:
/// <list type="bullet">
/// <item>a null text value becomes empty text, which the model's <c>Validate()</c> reports where it matters;</item>
/// <item>a null object or list keeps its default, such as a new <see cref="LoadProfile"/> or an empty list;</item>
/// <item>a null item in a list is dropped;</item>
/// <item>a null, or a missing value, where the model has no default, such as a result's plan, is a
/// <see cref="JsonException"/>, which every caller already reports as invalid JSON.</item>
/// </list>
/// Properties the model declares nullable keep their nulls.
/// <para>
/// A number too large for a double, such as <c>1e309</c>, is read as infinity, which no later step can write back
/// out as JSON, so it is a <see cref="JsonException"/> too.
/// </para>
/// </summary>
public static class JsonInput
{
    /// <summary>Applies the rules to <paramref name="options"/>, which must not have been used yet.</summary>
    public static JsonSerializerOptions Apply(JsonSerializerOptions options)
    {
        // Makes a null for a constructor parameter (a positional record) a JsonException; the modifier below handles
        // properties, which it makes accept null so that the rules above apply instead.
        options.RespectNullableAnnotations = true;
        options.TypeInfoResolver = (options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()).WithAddedModifier(Modify);
        options.Converters.Add(new FiniteDoubleConverter());
        return options;
    }

    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            double value;
            if (reader.TokenType != JsonTokenType.String)
            {
                value = reader.GetDouble();
            }
            else if ((options.NumberHandling & JsonNumberHandling.AllowReadingFromString) == 0
                || !double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new JsonException("The text isn't a number.");
            }

            return double.IsFinite(value) ? value : throw new JsonException($"The number {value} is too large.");
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private static void Modify(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var required = new List<JsonPropertyInfo>();
        var lists = new List<JsonPropertyInfo>();
        foreach (var property in info.Properties)
        {
            if (property.PropertyType.IsValueType)
            {
                continue;
            }

            if (!property.IsGetNullable && property.Get is not null)
            {
                required.Add(property);
            }

            if (property.Get is not null && HoldsReferences(property.PropertyType))
            {
                lists.Add(property);
            }

            // Writing never fails over a null; only reading is checked.
            property.IsGetNullable = true;
            if (property.Set is not { } set)
            {
                continue;
            }

            var nullable = property.IsSetNullable;
            var list = HoldsReferences(property.PropertyType);
            if (nullable && !list)
            {
                continue;
            }

            property.IsSetNullable = true;
            var get = property.Get;
            var name = property.Name;
            var text = property.PropertyType == typeof(string);
            property.Set = (target, value) =>
            {
                if (value is null)
                {
                    if (nullable)
                    {
                        set(target, null);
                    }
                    else if (text)
                    {
                        set(target, string.Empty);
                    }
                    else if (get?.Invoke(target) is null)
                    {
                        throw new JsonException($"'{name}' can't be null.");
                    }

                    return;
                }

                set(target, list ? WithoutNulls(value) : value);
            };
        }

        // A value left out of the JSON keeps its default, but one with no default, such as a positional record's
        // parameter, would stay null. A positional record's lists are passed to its constructor, not set, so their
        // null items are dropped here.
        if (required.Count > 0 || lists.Count > 0)
        {
            var previous = info.OnDeserialized;
            info.OnDeserialized = target =>
            {
                previous?.Invoke(target);
                foreach (var property in required)
                {
                    if (property.Get!(target) is null)
                    {
                        throw new JsonException($"'{property.Name}' is missing.");
                    }
                }

                foreach (var property in lists)
                {
                    if (property.Get!(target) is IEnumerable items && WithoutNulls(items) != items)
                    {
                        throw new JsonException($"'{property.Name}' can't have null in it.");
                    }
                }
            };
        }
    }

    // A list of objects or text, whose null items can be dropped; not a list of numbers that may be null on purpose.
    private static bool HoldsReferences(Type type) =>
        type != typeof(string) && type.GetInterfaces().Append(type).Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && !i.GetGenericArguments()[0].IsValueType);

    private static object WithoutNulls(object value)
    {
        switch (value)
        {
            case Array array when array.GetType().GetElementType() is { IsValueType: false } element && Contains(array):
            {
                var kept = array.Cast<object?>().Where(item => item is not null).ToArray();
                var result = Array.CreateInstance(element, kept.Length);
                Array.Copy(kept, result, kept.Length);
                return result;
            }

            case IList { IsReadOnly: false, IsFixedSize: false } items:
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    if (items[i] is null)
                    {
                        items.RemoveAt(i);
                    }
                }

                return items;
            default:
                return value;
        }
    }

    private static bool Contains(Array array)
    {
        foreach (var item in array)
        {
            if (item is null)
            {
                return true;
            }
        }

        return false;
    }
}
