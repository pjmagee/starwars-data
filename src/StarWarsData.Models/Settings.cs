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
    
    // New model: download pages by enumerating ALL templates (namespace 10) and
    // for each template pulling up to PagesPerTemplate pages that embed it.
    // This deprecates the separate raw infobox download step; infobox data is
    // later extracted from the stored raw pages.
    public bool DownloadPagesByTemplate { get; set; } = false;

    // Target number of pages to fetch per template when DownloadPagesByTemplate is enabled.
    public int PagesPerTemplate { get; set; } = 100;

    // Automatically run infobox extraction after page download completes (if enabled).
    public bool AutoExtractInfoboxes { get; set; } = false;

    // When non-empty, only templates whose title matches one of these names (after "Template:" prefix)
    // will be processed during template-driven page download. Empty means process all templates.
    public IEnumerable<string> TargetTemplates { get; set; } = [];

    // Templates to skip entirely during template-driven page download (real-world/out-of-universe infoboxes).
    public IEnumerable<string> ExcludedTemplates { get; set; } = [];

    // Page categories that indicate a page should be skipped (e.g. real-world, meta, OOU articles).
    public IEnumerable<string> ExcludedCategories { get; set; } = [];
}
