namespace StarWarsData.Models;

public class SettingsOptions
{
    public const string Settings = "Settings";

    /// <summary>
    /// Whether Hangfire recurring jobs and background server are enabled.
    /// Disable in dev to avoid duplicate jobs when sharing a database with production.
    /// </summary>
    public bool HangfireEnabled { get; set; } = true;

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

    public IEnumerable<string> TimelineCollections { get; set; } = [];

    /// <summary>
    /// The model to use for relationship extraction (high-volume, low-cost)
    /// </summary>
    public string RelationshipAnalystModel { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// Max pages to process per graph builder batch run
    /// </summary>
    public int GraphBuilderBatchSize { get; set; } = 100;

    // ── Databases ──
    // All app data lives in one database; Hangfire gets its own (it manages its schema)

    /// <summary>Single unified database for all application collections.</summary>
    public string DatabaseName { get; set; } = "starwars";

    /// <summary>Separate database for Hangfire (it creates its own collections).</summary>
    public string HangfireDb { get; set; } = "starwars-hangfire";

    // ── Legacy DB names (kept for backward compat during migration) ──
    [Obsolete("Use DatabaseName")] public string PagesDb { get => DatabaseName; set { } }
    [Obsolete("Use DatabaseName")] public string TimelineEventsDb { get => DatabaseName; set { } }
    [Obsolete("Use DatabaseName")] public string ChatSessionsDb { get => DatabaseName; set { } }
    [Obsolete("Use DatabaseName")] public string CharacterTimelinesDb { get => DatabaseName; set { } }
    [Obsolete("Use DatabaseName")] public string RelationshipGraphDb { get => DatabaseName; set { } }
    [Obsolete("Use DatabaseName")] public string TerritoryControlDb { get => DatabaseName; set { } }
}

/// <summary>
/// Central registry of all MongoDB collection names.
/// One place to see the full namespace layout.
/// </summary>
public static class Collections
{
    // ── Raw pages ──
    public const string Pages = "raw.pages";
    public const string JobState = "raw.job_state";

    // ── Timeline events (one collection per infobox type) ──
    // Accessed via: db.GetCollection<TimelineEvent>($"timeline.{typeName}")
    public const string TimelinePrefix = "timeline.";

    // ── Knowledge graph ──
    public const string KgNodes = "kg.nodes";
    public const string KgEdges = "kg.edges";
    public const string KgCrawlState = "kg.crawl_state";
    public const string KgBatchJobs = "kg.batch_jobs";
    public const string KgLabels = "kg.labels";
    public const string KgChunks = "kg.chunks";

    // Infobox-derived edges (deterministic, no LLM)
    public const string KgEdgesInfobox = "kg.edges_infobox";

    // ── AI-generated content ──
    public const string GenaiCharacterTimelines = "genai.character_timelines";
    public const string GenaiCharacterCheckpoints = "genai.character_checkpoints";
    public const string GenaiCharacterProgress = "genai.character_progress";

    // ── Chat ──
    public const string ChatSessions = "chat.sessions";
    public const string UserSettings = "chat.user_settings";

    // ── Territory control ──
    public const string TerritorySnapshots = "territory.snapshots";

    // ── Admin ──
    public const string JobToggles = "admin.job_toggles";
}
