namespace StarWarsData.Models.Queries;

/// <summary>
/// Initial payload for the Galaxy Events page: timeline range, available lenses, base galaxy regions.
/// </summary>
public class GalaxyEventsOverview
{
    public int GridColumns { get; set; } = 26;
    public int GridRows { get; set; } = 20;
    public List<MapV2Region> Regions { get; set; } = [];
    public TimelineRange TimelineRange { get; set; } = new();
    public List<string> AvailableLenses { get; set; } = [];

    /// <summary>Per-lens document counts for summary cards.</summary>
    public List<LensSummary> LensSummaries { get; set; } = [];

    /// <summary>Eras derived from the Era timeline collection.</summary>
    public List<EraRange> Eras { get; set; } = [];
}

public class LensSummary
{
    public string Lens { get; set; } = "";
    public int TotalCount { get; set; }

    /// <summary>How many docs in this lens have a location-relevant property.</summary>
    public int WithLocationCount { get; set; }
}

public class EraRange
{
    public string Name { get; set; } = "";

    /// <summary>Sort-key start (negative = BBY, positive = ABY).</summary>
    public int Start { get; set; }

    /// <summary>Sort-key end.</summary>
    public int End { get; set; }
}

public class TimelineRange
{
    public int MinYear { get; set; }
    public int MaxYear { get; set; }

    /// <summary>Distinct years available in the data, sorted.</summary>
    public List<TimelineYearEntry> Years { get; set; } = [];
}

public class TimelineYearEntry
{
    public int Year { get; set; }
    public string Demarcation { get; set; } = "BBY";

    /// <summary>Combined sort key: negative for BBY, positive for ABY.</summary>
    public int SortKey { get; set; }
    public int EventCount { get; set; }
}

/// <summary>
/// Per-lens year density data for the sparkline / density bar.
/// </summary>
public class LensDensity
{
    public string Lens { get; set; } = "";
    public List<DensityBucket> Buckets { get; set; } = [];
}

public class DensityBucket
{
    public int SortKey { get; set; }
    public int Count { get; set; }

    /// <summary>Normalized 0.0-1.0 within this lens.</summary>
    public double Intensity { get; set; }
}

/// <summary>
/// Paginated list of events for the event browser drawer.
/// </summary>
public class GalaxyEventList
{
    public string Lens { get; set; } = "";
    public List<GalaxyEventListItem> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class GalaxyEventListItem
{
    public string Title { get; set; } = "";
    public int Year { get; set; }
    public string Demarcation { get; set; } = "BBY";
    public int SortKey { get; set; }
    public string? Place { get; set; }
    public string Collection { get; set; } = "";
    public bool HasLocation { get; set; }
}

/// <summary>
/// A layer of events mapped to grid cells for a specific lens and time window.
/// </summary>
public class GalaxyEventLayer
{
    public string Lens { get; set; } = "";
    public int YearStart { get; set; }
    public int YearEnd { get; set; }
    public string Demarcation { get; set; } = "BBY";
    public List<GalaxyEventCell> Cells { get; set; } = [];
    public int TotalEvents { get; set; }
    public int MappedEvents { get; set; }
}

public class GalaxyEventCell
{
    public int Col { get; set; }
    public int Row { get; set; }
    public int Count { get; set; }

    /// <summary>Normalized intensity 0.0-1.0 for heatmap rendering.</summary>
    public double Intensity { get; set; }
    public string? Region { get; set; }
    public List<GalaxyEventMarker> Events { get; set; } = [];
}

public class GalaxyEventMarker
{
    public string Title { get; set; } = "";
    public string Collection { get; set; } = "";
    public int Year { get; set; }
    public string Demarcation { get; set; } = "BBY";
    public string? Place { get; set; }
    public string? Outcome { get; set; }
    public string? ImageUrl { get; set; }
}
