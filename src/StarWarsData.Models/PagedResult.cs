namespace StarWarsData.Models;

public class PagedResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<Record> Items { get; set; }
}

public class PagedResult<T>
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<T> Items { get; set; }
}

public class GroupedTimelineResult
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<GroupedTimelines> Items { get; set; }
}

public class GroupedTimelines
{
    public string Year { get; set; }
    public List<TimelineEvent> Events { get; set; }
}