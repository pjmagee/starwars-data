using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarWarsData.Services;

/// <summary>
/// Tolerant JSON coercion for the <see cref="ComponentToolkit"/> render_* tool arguments.
///
/// <para>
/// The LLM routinely emits scalar shapes that don't match the tool parameters' declared
/// CLR types, and System.Text.Json rejects the mismatch by default — which throws during
/// M.E.AI argument binding, becomes an <c>Error: Function failed…</c> string, and (because
/// AGUI hosting writes that raw to the wire) surfaces in the browser as the Design-041
/// "A tool returned a malformed response" banner that kills the whole turn. Observed
/// real-world mismatches (each from an actual /ask failure):
/// </para>
/// <list type="bullet">
///   <item><c>xAxisLabels:[22,21,20,19]</c> — years as JSON numbers into <c>List&lt;string&gt;</c>.</item>
///   <item><c>rows:[["Sword",3]]</c> — numeric/boolean cell into <c>List&lt;List&lt;string&gt;&gt;</c>.</item>
///   <item><c>series[].data:[2,null]</c> — null gap into <c>List&lt;double&gt;</c>.</item>
///   <item><c>timeSeries[].data[].x:"-0022-01-01T00:00:00Z"</c> — a BCE ISO date into
///         <c>DateTime</c> (in-universe BBY years are pre-year-0, which STJ can't parse).</item>
/// </list>
///
/// <para>
/// We fix the COMMON cases at the only layer that affects argument binding: the
/// <see cref="JsonSerializerOptions"/> passed to <c>AIFunctionFactory.Create</c>. Attributes
/// on the descriptor POCOs do NOT help — the failing types are the tool METHOD parameters,
/// and the descriptors are only populated server-side after binding succeeds. The tolerant
/// options are a CLONE of the AGUI options (preserving the shared type-info resolver the
/// existing comment requires) with three scalar converters added, so the coercion is scoped
/// to the ComponentToolkit and changes nothing for other toolkits or the wire shape.
/// </para>
///
/// <para>
/// The remaining safety net — any un-anticipated shape that STILL throws degrades to a JSON
/// error result instead of propagating — lives in <c>ToolCallBudgetMiddleware</c>'s render_*
/// catch, the function-invocation middleware that provably wraps every tool call. (An
/// AIFunction-subclass wrapper at this layer does NOT fire in the AGUI/FIC invocation path.)
/// </para>
/// </summary>
static class ComponentToolkitJsonCoercion
{
    /// <summary>
    /// Clone <paramref name="baseOptions"/> and add scalar-coercion converters. The clone
    /// keeps the base options' <see cref="JsonSerializerOptions.TypeInfoResolver"/> and every
    /// other setting; only string / double / DateTime element binding becomes tolerant.
    /// </summary>
    public static JsonSerializerOptions CreateTolerant(JsonSerializerOptions baseOptions)
    {
        var options = new JsonSerializerOptions(baseOptions);
        options.Converters.Add(new ScalarToStringConverter());
        options.Converters.Add(new TolerantDoubleConverter());
        options.Converters.Add(new TolerantDateTimeConverter());
        return options;
    }

    /// <summary>
    /// Reads any JSON scalar (string / number / bool / null) as a <see cref="string"/>.
    /// Numbers render without a spurious decimal point (22 → "22"). Strings, null, and
    /// property names pass through unchanged, so this is a no-op for genuinely-string
    /// parameters (title, mobileSummary, column headers).
    /// </summary>
    sealed class ScalarToStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Null => null,
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Number => ReadNumber(ref reader),
                // Arrays/objects where a string was expected: keep the raw JSON rather than throw.
                _ => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            };

        static string ReadNumber(ref Utf8JsonReader reader)
        {
            if (reader.TryGetInt64(out var l))
                return l.ToString(CultureInfo.InvariantCulture);
            if (reader.TryGetDouble(out var d))
                return d.ToString(CultureInfo.InvariantCulture);
            return reader.GetDecimal().ToString(CultureInfo.InvariantCulture);
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);

        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WritePropertyName(value);
    }

    /// <summary>
    /// Reads a <see cref="double"/> from a JSON number, a numeric string ("15"), or a null
    /// gap (→ 0). Covers <c>series[].data</c> where the model quotes numbers or leaves holes.
    /// </summary>
    sealed class TolerantDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
                JsonTokenType.String => 0,
                JsonTokenType.Null => 0,
                _ => 0,
            };

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    /// <summary>
    /// Reads a <see cref="DateTime"/> tolerantly: a normal ISO timestamp, a bare year
    /// ("1977" or "-22"), or anything STJ's strict parser rejects (e.g. a BCE ISO string
    /// like "-0022-01-01T00:00:00Z" that the model emits for BBY years) falls back to
    /// <see cref="DateTime.MinValue"/> rather than throwing. The TimeSeries renderer keys
    /// off the y-values and the accompanying label text, so a coerced x-date never produces
    /// a wrong chart — but it does stop the whole turn from dying.
    /// </summary>
    sealed class TolerantDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number when reader.TryGetInt32(out var y) && y is >= -9999 and <= 9999:
                    return SafeYear(y);
                case JsonTokenType.String:
                    var s = reader.GetString();
                    if (string.IsNullOrWhiteSpace(s))
                        return DateTime.MinValue;
                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                        return dt;
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
                        return SafeYear(year);
                    return DateTime.MinValue;
                default:
                    reader.Skip();
                    return DateTime.MinValue;
            }
        }

        // CE years map to that Jan 1; non-positive (BCE / BBY) years can't be a real
        // DateTime, so clamp to MinValue — the label carries the human-readable year.
        static DateTime SafeYear(int year) => year >= 1 ? new DateTime(year, 1, 1) : DateTime.MinValue;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
