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