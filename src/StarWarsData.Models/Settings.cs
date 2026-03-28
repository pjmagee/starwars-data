namespace StarWarsData.Models;

public class SettingsOptions
{
    public const string Settings = "Settings";

    /// <summary>
    /// The database for Hangfire jobs
    /// </summary>
    public string HangfireDb { get; set; } = null!;

    /// <summary>
    /// The database for wookiepedia downloaded pages
    /// </summary>
    public string PagesDb { get; set; } = null!;

    /// <summary>
    /// The database for timeline events
    /// </summary>
    public string TimelineEventsDb { get; set; } = null!;

    /// <summary>
    /// The database for user chat sessions
    /// </summary>
    public string ChatSessionsDb { get; set; } = "starwars-chat-sessions";

    /// <summary>
    /// The base URL for Wookieepedia API
    /// </summary>
    public string StarWarsBaseUrl { get; set; } = null!;

    public int PageNamespace { get; set; } = 0;

    public int PageStart { get; set; } = 1;

    public int PageLimit { get; set; } = 500;

    public bool FirstPageOnly { get; set; } = false;

    public string OpenAiKey { get; set; } = null!;

    public string OpenAiModel { get; set; } = "gpt-5-mini";

    /// <summary>
    /// The model to use for character timeline generation (needs large context window)
    /// </summary>
    public string CharacterTimelineModel { get; set; } = "gpt-5";

    /// <summary>
    /// The database for AI-generated character timelines
    /// </summary>
    public string CharacterTimelinesDb { get; set; } = "starwars-character-timelines";

    public IEnumerable<string> TimelineCollections { get; set; } = [];

    /// <summary>
    /// The database for the persistent relationship graph (edges, crawl state, label registry)
    /// </summary>
    public string RelationshipGraphDb { get; set; } = "starwars-relationship-graph";

    /// <summary>
    /// The model to use for relationship extraction (high-volume, low-cost)
    /// </summary>
    public string RelationshipAnalystModel { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// Max pages to process per graph builder batch run
    /// </summary>
    public int GraphBuilderBatchSize { get; set; } = 100;
}
