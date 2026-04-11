using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace StarWarsData.Services;

/// <summary>
/// Sanitization helpers for user/LLM strings that flow into MongoDB queries.
///
/// BSON distinguishes two string flavors: length-prefixed Strings (null bytes legal)
/// and NUL-terminated CStrings (field names, regex patterns, regex options, code).
/// Any CString containing a \0 causes MongoDB.Driver to throw
/// "A CString cannot contain null bytes. (Parameter 'value')".
///
/// LLMs and scraped content can occasionally emit U+0000 and other C0 control
/// characters. This class strips them at the boundary so they never reach Mongo.
/// </summary>
public static class MongoSafe
{
    /// <summary>
    /// Strip characters that are illegal in BSON CStrings or cause parser surprises:
    /// U+0000 (the actual culprit) and the rest of the C0 control range except \t, \n, \r.
    /// Also trims surrogate halves, which throw on UTF-8 encode.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!NeedsSanitize(value))
            return value;

        var buffer = new char[value.Length];
        var j = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (IsUnsafe(c))
                continue;
            // Drop unpaired surrogates — they blow up UTF-8 encode.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    buffer[j++] = c;
                    buffer[j++] = value[++i];
                }
                continue;
            }
            if (char.IsLowSurrogate(c))
                continue;
            buffer[j++] = c;
        }
        return new string(buffer, 0, j);
    }

    /// <summary>
    /// Sanitize every string in a sequence and drop empties.
    /// </summary>
    public static IEnumerable<string> SanitizeAll(IEnumerable<string?>? values) => values is null ? [] : values.Select(Sanitize).Where(s => s.Length > 0);

    /// <summary>
    /// Build a <see cref="BsonRegularExpression"/> from user/LLM input with null bytes stripped.
    /// Set <paramref name="escape"/> to true when <paramref name="pattern"/> is a literal to match
    /// (not a regex expression) — it will be passed through <see cref="Regex.Escape(string)"/>.
    /// </summary>
    public static BsonRegularExpression Regex(string pattern, string options = "i", bool escape = false)
    {
        var safe = Sanitize(pattern);
        if (escape)
            safe = System.Text.RegularExpressions.Regex.Escape(safe);
        return new BsonRegularExpression(safe, options);
    }

    static bool NeedsSanitize(string value)
    {
        foreach (var c in value)
        {
            if (IsUnsafe(c) || char.IsSurrogate(c))
                return true;
        }
        return false;
    }

    static bool IsUnsafe(char c) => c == '\0' || (c < 0x20 && c != '\t' && c != '\n' && c != '\r') || c == 0x7F;
}
