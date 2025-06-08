namespace StarWarsData.Models;

public class SettingsOptions
{
    public const string Settings = "Settings";
    public string DataDirectory { get; set; } = null!;
    
    public string LogsDirectory { get; set; } = null!;
    public string MongoConnectionString { get; set; } = null!;
    public string RawDb { get; set; } = null!;
    public string StructuredDb { get; set; } = null!;
    
    public string CosmosDbConnectionString { get; set; } = null!;
    public string StarWarsBaseUrl { get; set; } = null!;
    public int PageNamespace { get; set; } = 0;
    public int PageStart { get; set; } = 1;
    public int PageLimit { get; set; } = 500;
    public bool FirstPageOnly { get; set; } = true;
    public string OpenAiKey { get; set; } = null!;
    public IEnumerable<string> TimelineCollections { get; set; } = null!;
}