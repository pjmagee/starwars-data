using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Queries;

public class PagedResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<Infobox> Items { get; set; } = Enumerable.Empty<Infobox>();
}

public class PagedResult<T>
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<T> Items { get; set; } = Enumerable.Empty<T>();
}

public class GroupedTimelineResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<GroupedTimelines> Items { get; set; } = Enumerable.Empty<GroupedTimelines>();
}

public class GroupedTimelines
{
    public string Year { get; set; } = string.Empty;
    public List<TimelineEvent> Events { get; set; } = new();
}
