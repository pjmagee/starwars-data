using MudBlazor;
using StarWarsData.Models.Entities;

namespace StarWarsData.Frontend.Components.Shared;

/// <summary>
/// Display helpers shared by the Knowledge Graph surfaces (KnowledgeGraph list page,
/// NodeDetailPanel shared component, KnowledgeGraphNodeDetail page). Pulled into a
/// static class so the same formatting (year ranges, facet roles, temporal colours,
/// spatial deep-link routing) is sourced from one place instead of being copy-pasted
/// across components.
/// </summary>
public static class KnowledgeGraphFormatters
{
    /// <summary>
    /// Direct spatial KG types — those that have a <c>/galaxy-map/{pageId}</c> deep
    /// link (per Design-031). Indirect spatial linking (Battle → location) is
    /// Phase 2 of Design-030 and lives in the citation resolver.
    /// </summary>
    public static bool IsSpatialKind(string? type) => type is "System" or "CelestialBody" or "Sector" or "Region" or "TradeRoute";

    /// <summary>
    /// Design-032: when the node whose edges we're viewing is itself an event
    /// (Battle, Mission, …), carry its pageId into the galaxy-map deep link as
    /// <c>?event=</c> so the spatial target lands with the event highlighted and
    /// the round-trip (KG event → location → event still in view) holds.
    /// </summary>
    public static string BuildGalaxyMapHref(int locationId, string? sourceType, int sourceId) =>
        SpatialEventLabels.EventFamily.Contains(sourceType ?? string.Empty) ? $"/galaxy-map/{locationId}?event={sourceId}" : $"/galaxy-map/{locationId}";

    public static string FormatYearRange(int? fromYear, int? toYear)
    {
        if (!fromYear.HasValue && !toYear.HasValue)
            return "—";
        var f = fromYear.HasValue ? FormatYear(fromYear.Value) : "?";
        var t = toYear.HasValue ? FormatYear(toYear.Value) : "?";
        return f == t ? f : $"{f} → {t}";
    }

    public static string FormatYear(int year) =>
        year < 0 ? $"{-year} BBY"
        : year > 0 ? $"{year} ABY"
        : "0 BBY/ABY";

    /// <summary>Render a UTC timestamp as a coarse "X ago" string for last-updated captions.</summary>
    public static string FormatRelativeTime(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalSeconds < 60)
            return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30)
            return $"{(int)diff.TotalDays}d ago";
        return utc.ToString("yyyy-MM-dd");
    }

    public static string FormatTypeName(string? type) =>
        type switch
        {
            null or "" => "",
            "CelestialBody" => "Planet / Body",
            _ => System.Text.RegularExpressions.Regex.Replace(type, "(?<!^)([A-Z])", " $1"),
        };

    public static string FormatPropertyKey(string? key) => key?.Replace("_", " ") ?? "";

    public static string FormatFacetRole(string semantic)
    {
        var role = semantic.Contains('.') ? semantic[(semantic.LastIndexOf('.') + 1)..] : semantic;
        return role switch
        {
            "start" => semantic.StartsWith("lifespan") ? "Born"
            : semantic.StartsWith("conflict") ? "Began"
            : semantic.StartsWith("construction") ? "Built"
            : semantic.StartsWith("creation") ? "Created"
            : semantic.StartsWith("institutional") ? "Established"
            : semantic.StartsWith("publication") ? "Released"
            : "Start",
            "end" => semantic.StartsWith("lifespan") ? "Died"
            : semantic.StartsWith("conflict") ? "Ended"
            : semantic.StartsWith("construction") ? "Destroyed"
            : semantic.StartsWith("creation") ? "Destroyed"
            : semantic.StartsWith("institutional") ? "Dissolved"
            : semantic.StartsWith("publication") ? "Ended"
            : "End",
            "point" => "Date",
            "release" => "Released",
            "rebuilt" => "Rebuilt",
            "reorganized" => "Reorganized",
            "restored" => "Restored",
            "fragmented" => "Fragmented",
            "suspended" => "Suspended",
            "discovered" => "Discovered",
            _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(role),
        };
    }

    public static Color GetFacetColor(string semantic)
    {
        var role = semantic.Contains('.') ? semantic[(semantic.LastIndexOf('.') + 1)..] : semantic;
        return role switch
        {
            "start" or "release" => Color.Success,
            "end" => Color.Error,
            "point" => Color.Primary,
            "reorganized" or "restored" => Color.Warning,
            "fragmented" or "suspended" => Color.Secondary,
            "discovered" or "rebuilt" => Color.Info,
            _ => Color.Default,
        };
    }

    /// <summary>
    /// Strip the trailing <c>/revision/...</c> path Wookieepedia appends to every Fandom
    /// asset URL — the bare URL renders fine and avoids a 301 hop on every image load.
    /// </summary>
    public static string StripRevision(string url)
    {
        var idx = url.IndexOf("/revision", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? url[..idx] : url;
    }
}
