using StarWarsData.Models.Mongo;

namespace StarWarsData.Models;

public class Loaded
{
    public int PageId { get; set; }
    public FileInfo File { get; set; }= null!;
    public Record Record { get; set; } = null!;
    public string Url { get; set; }= null!;
    public bool Processed { get; set; }
    public HashSet<string> Links { get; set; } = null!;
}