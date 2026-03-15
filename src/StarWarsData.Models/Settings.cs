namespace StarWarsData.Models;

public class SettingsOptions
{
    public const string Settings = "Settings";

    /// <summary>
    /// The database for Hangfire jobs
    /// </summary>
    public string HangfireDb { get; set; } = null!;  

    /// <summary>
    /// The database for wookiepedia downloaded infoboxes.
    /// NOT extracted from pages.
    /// </summary>    
    public string InfoboxDb { get; set; } = null!;
    
    /// <summary>
    /// The database for wookiepedia downloaded pages
    /// </summary>
    public string PagesDb { get; set; } = null!;

    /// <summary>
    /// The database for infoboxes extracted from pages
    /// </summary>
    public string PageInfoboxDb { get; set; } = null!;

    /// <summary>
    /// The database for structured data
    /// </summary>
    public string StructuredDb { get; set; } = null!;

    /// <summary>
    /// The database for timeline events
    /// </summary>
    public string TimelineEventsDb { get; set; } = null!;

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

    public IEnumerable<string> TimelineCollections { get; set; } = [];
    
    // Automatically run infobox extraction after page download completes.
    public bool AutoExtractInfoboxes { get; set; } = false;

    // Infobox templates to save — pages whose infobox template is not in this list are skipped.
    public IEnumerable<string> TargetTemplates { get; set; } = [];

    // Infobox templates to always skip (real-world/out-of-universe infoboxes).
    public IEnumerable<string> ExcludedTemplates { get; set; } = [];

    // Page categories that indicate a page should be skipped (e.g. real-world, meta, OOU articles).
    public IEnumerable<string> ExcludedCategories { get; set; } = [];
}
