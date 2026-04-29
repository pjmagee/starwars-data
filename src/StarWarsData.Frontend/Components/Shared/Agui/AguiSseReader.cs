using System.Runtime.CompilerServices;

namespace StarWarsData.Frontend.Components.Shared.Agui;

/// <summary>
/// Reads AGUI server-sent events off an HTTP response stream and yields the
/// raw JSON payloads (one per <c>data:</c> line). Stops on the <c>[DONE]</c>
/// sentinel. Callers parse each payload with <see cref="System.Text.Json.JsonDocument"/>
/// and dispatch on the <c>type</c> field.
/// </summary>
public static class AguiSseReader
{
    public static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;
            if (line.Length == 0)
                continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            var json = line["data:".Length..].Trim();
            if (json == "[DONE]")
                break;

            yield return json;
        }
    }
}
