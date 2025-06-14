namespace StarWarsData.Models;

public class SettingsOptions
{
    public const string Settings = "Settings";
    public string HangfireDb { get; set; } = null!;
    public string RawDb { get; set; } = null!;
    public string InfoboxDb { get; set; } = null!;
    public string StructuredDb { get; set; } = null!;
    public string TimelineEventsDb { get; set; } = null!;
    public string StarWarsBaseUrl { get; set; } = null!;
    public int PageNamespace { get; set; } = 0;
    public int PageStart { get; set; } = 1;
    public int PageLimit { get; set; } = 500;
    public bool FirstPageOnly { get; set; } = false;
    public string OpenAiKey { get; set; } = null!;
    public IEnumerable<string> TimelineCollections { get; set; } = null!;
}
