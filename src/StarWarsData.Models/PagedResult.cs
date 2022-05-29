namespace StarWarsData.Models;

public class PagedResult
{
    public long Count { get; set; }
    public int Page { get; set; }
    public int Size { get; set; }
    public IEnumerable<Record> Items { get; set; }
}