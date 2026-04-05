namespace StarWarsData.Services;

/// <summary>
/// Shared color lookup for galaxy-map and territory-control rendering.
/// <para>
/// Iconic Star Wars factions get brand-consistent colors (Empire grey, Republic blue,
/// Alliance orange, etc.). Any other faction falls through to a deterministic hash
/// over a fixed palette — the same faction name always maps to the same color.
/// </para>
/// <para>
/// Uses <see cref="string.GetHashCode(StringComparison)"/> with <see cref="StringComparison.OrdinalIgnoreCase"/>
/// for the hash, which is stable across processes (unlike the parameterless overload,
/// which is randomized per-process in modern .NET).
/// </para>
/// </summary>
public static class FactionColorPalette
{
    /// <summary>
    /// Hand-picked colors for well-known factions where brand consistency matters.
    /// Case-insensitive lookup.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Curated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Galactic Republic"] = "#3b82f6",
        ["Confederacy of Independent Systems"] = "#ef4444",
        ["Galactic Empire"] = "#6b7280",
        ["Alliance to Restore the Republic"] = "#f97316",
        ["New Republic"] = "#22c55e",
        ["First Order"] = "#991b1b",
        ["Resistance"] = "#f59e0b",
        ["Hutt Clan"] = "#a8722a",
        ["Chiss Ascendancy"] = "#1e3a5f",
        ["Nihil"] = "#8b5cf6",
    };

    /// <summary>
    /// Fallback palette for factions not present in <see cref="Curated"/>.
    /// Selected by a stable hash of the faction name so renders are deterministic
    /// across processes and builds.
    /// </summary>
    public static readonly IReadOnlyList<string> Fallback =
    [
        "#3b82f6",
        "#ef4444",
        "#6b7280",
        "#f97316",
        "#22c55e",
        "#991b1b",
        "#f59e0b",
        "#a8722a",
        "#1e3a5f",
        "#8b5cf6",
        "#ec4899",
        "#14b8a6",
        "#84cc16",
        "#d946ef",
        "#06b6d4",
    ];

    /// <summary>
    /// Get the color for a faction. Returns the curated color if known,
    /// otherwise a deterministic color from <see cref="Fallback"/>.
    /// </summary>
    public static string GetColor(string faction)
    {
        if (Curated.TryGetValue(faction, out var color))
            return color;
        return Fallback[Math.Abs(faction.GetHashCode(StringComparison.OrdinalIgnoreCase)) % Fallback.Count];
    }
}
