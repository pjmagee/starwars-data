using StarWarsData.Models.Entities;

namespace StarWarsData.Models;

public class Loaded
{
    public int PageId { get; set; }
    public InfoboxRecord Record { get; set; } = null!;
    public string Url { get; set; }= null!;
    public bool Processed { get; set; }
    public HashSet<string> Links { get; set; } = null!;
}