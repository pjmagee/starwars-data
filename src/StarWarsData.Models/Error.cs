namespace StarWarsData.Models;

public class Error
{
    public Dictionary<string, object> Record { get; set; } = null!;
    public string Message { get; set; } = null!;
}
