namespace StarWarsData.Models;

public class Settings
{
    public string DataDirectory { get; set; } = null!;
    
    public string LogsDirectory { get; set; } = null!;

    public string MongoDbUri { get; set; } = null!;
    
    public string MongoDbName { get; set; } = null!;
    
    public string StarWarsBaseUrl { get; set; } = null!;
    public int PageNamespace { get; set; } = 0;

    public int PageStart { get; set; } = 1;
    public int PageLimit { get; set; } = 500;

    public bool FirstPageOnly { get; set; } = true;
    
}