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

    /// <summary>
    /// Org-scoped OpenAI Admin API key (sk-admin-…) used by the Admin app's nightly
    /// spend sync to call <c>/v1/organization/costs</c>. Distinct from <see cref="OpenAiKey"/>
    /// (the project key used for inference) — admin keys are read-only billing scope and
    /// must never be shipped to ApiService or Frontend. Optional: when null/empty the
    /// nightly sync exits silently and the public /api/costs endpoint serves whatever
    /// data is already cached.
    /// </summary>
    public string? OpenAiAdminKey { get; set; }

    public string OpenAiModel { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// The model to use for character timeline generation (needs large context window)
    /// </summary>
    public string CharacterTimelineModel { get; set; } = "gpt-5.4";

    public IEnumerable<string> TimelineCollections { get; set; } = [];

    /// <summary>
    /// The model to use for relationship extraction (high-volume, low-cost)
    /// </summary>
    public string RelationshipAnalystModel { get; set; } = "gpt-5.4-mini";

    /// <summary>
    /// Max pages to process per graph builder batch run
    /// </summary>
    public int GraphBuilderBatchSize { get; set; } = 100;

    // ── Holocron agent (Phase 2 — Design-018) ──

    /// <summary>
    /// Master switch for the Holocron daily pass. Default <c>false</c> — opt-in for safety.
    /// When disabled, <see cref="StarWarsData.Services.AI.Agents.HolocronAgent.RunDailyPassAsync"/>
    /// returns immediately without scheduling LLM calls.
    /// </summary>
    public bool HolocronEnabled { get; set; } = false;

    /// <summary>The model used for Holocron enhancement calls. Reasoning-capable model recommended.</summary>
    public string HolocronModel { get; set; } = "gpt-5.4";

    /// <summary>Number of nodes to enhance per daily pass. Kept low while we observe behaviour on dev.</summary>
    public int HolocronNodesPerPass { get; set; } = 10;

    /// <summary>
    /// Max 1-hop neighbours included in the agent's context window per enhancement call.
    /// Halved for outgoing/incoming each. Set high enough to capture every Lifecycle-tagged
    /// edge for typical character-class nodes — Anakin v1.5.1 missed his
    /// <c>apprentice_of → Sidious</c> FillGap because Sidious was 9th by weight and the
    /// previous default of 10 (halfLimit=5) clipped it. 60 → halfLimit=30 covers Anakin's
    /// 35 outgoing edges in full while staying bounded for Sidious-class super-hubs.
    /// </summary>
    public int HolocronMaxNeighborsForContext { get; set; } = 60;

    /// <summary>
    /// Article-chunk budget split across three sources. The total = own + linking + vector
    /// is the number of chunk excerpts the agent sees per enhancement call. Tune to balance
    /// context richness against token cost.
    /// </summary>
    public int HolocronOwnPageChunks { get; set; } = 3;

    /// <summary>
    /// How many chunks to pull from pages that LINK TO the target node (top-K incoming-edge
    /// sources, filtered to chunks where the target's name appears). Surfaces what *other*
    /// pages say about the target.
    /// </summary>
    public int HolocronLinkingPageChunks { get; set; } = 5;

    /// <summary>
    /// How many vector-similar chunks to pull from across the corpus (excluding the target's
    /// own page). Surfaces tangentially-related passages the link graph might not catch.
    /// Requires <c>SemanticSearchService</c> to be registered in DI; degrades to 0 otherwise.
    /// </summary>
    public int HolocronVectorChunks { get; set; } = 5;

    /// <summary>Max length (chars) of a chunk excerpt included in the prompt before truncation.</summary>
    public int HolocronMaxChunkExcerptLength { get; set; } = 600;

    // ── Database ──
    // All app data + Hangfire live in one database.
    // Hangfire collections are namespaced via its Prefix option (default "hangfire").

    // ── Rate Limiting ──

    /// <summary>Rate limit for anonymous users (requests per window). 0 = unlimited.</summary>
    public int RateLimitAnonymous { get; set; } = 3;

    /// <summary>Rate limit for authenticated users without BYOK (requests per window). 0 = unlimited.</summary>
    public int RateLimitAuthenticated { get; set; } = 10;

    /// <summary>Rate limit sliding window duration in minutes.</summary>
    public int RateLimitWindowMinutes { get; set; } = 30;

    /// <summary>Single unified database for all collections (app data + Hangfire).</summary>
    /// <remarks>Defaults to dev — production overrides via appsettings or env var Settings__DatabaseName.</remarks>
    public string DatabaseName { get; set; } = "starwars-dev";

    // ── Keycloak Admin API (service account for GDPR user deletion) ──

    /// <summary>Base URL of the Keycloak server (e.g. "https://auth.magaoidh.pro").</summary>
    public string KeycloakBaseUrl { get; set; } = "https://auth.magaoidh.pro";

    /// <summary>Keycloak realm name.</summary>
    public string KeycloakRealm { get; set; } = "starwars-data";

    /// <summary>Client ID of the confidential service-account client.</summary>
    public string KeycloakAdminClientId { get; set; } = "starwars-api-admin";

    /// <summary>Client secret for the service-account client.</summary>
    public string KeycloakAdminClientSecret { get; set; } = null!;
}

/// <summary>
/// Central registry of all MongoDB collection names.
/// One place to see the full namespace layout.
/// </summary>
public static class Collections
{
    /// <summary>
    /// URL prefix for infobox template values stored in raw.pages.
    /// e.g. "https://starwars.fandom.com/wiki/Template:Character"
    /// </summary>
    public const string TemplateUrlPrefix = "https://starwars.fandom.com/wiki/Template:";

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

    // ── Knowledge graph enrichments (Phase 2 — Holocron agent) ──
    // Append-only agent additions to the KG, joined into the *.enriched views at read time.
    // Phase 1 (InfoboxGraphService) NEVER touches these collections — separation of writers.
    // See eng/design/018-kg-enrichments-architecture.md.
    public const string KgEnrichments = "kg.enrichments";
    public const string KgEdgeEnrichments = "kg.edge_enrichments";
    public const string KgEvents = "kg.events";

    // ── Holocron async pipeline (Design-020) ──
    // Job lifecycle + per-(node, chunk) processing ledger powering change-aware re-runs.
    // Same separation-of-writers rule — Phase 1 never touches these.
    public const string KgEnrichmentJobs = "kg.enrichment_jobs";
    public const string KgNodeProcessedChunks = "kg.node_processed_chunks";

    /// <summary>
    /// Per-proposal audit trail — one row per unique post-dedup proposal recorded by the
    /// Holocron pipeline, capturing every stage's verdict (consolidator pre-flight,
    /// verifier, apply) so we can answer "why was proposal X rejected?" without re-running.
    /// Lifts the silent-rejection black box that bit Anakin's v1.5.2 run (122 unique →
    /// 48 applied with no per-proposal record of why 74 were dropped).
    /// </summary>
    public const string KgHolocronAudits = "kg.holocron_audits";

    // ── Knowledge graph enriched read views ──
    // Mongo views that left-join the base collections with active enrichments.
    // Read-only — created via Mongo migration 0010, not by the AppHost.
    public const string KgNodesEnriched = "kg.nodes.enriched";
    public const string KgEdgesEnriched = "kg.edges.enriched";

    public const string SearchChunks = "search.chunks";

    // ── AI-generated content ──
    public const string GenaiCharacterTimelines = "genai.character_timelines";
    public const string GenaiCharacterCheckpoints = "genai.character_checkpoints";
    public const string GenaiCharacterProgress = "genai.character_progress";

    /// <summary>
    /// Workflow checkpoint store for the Holocron async pipeline. Same shape as
    /// <see cref="GenaiCharacterCheckpoints"/> — written by <c>MongoCheckpointStore</c>
    /// during a Holocron run, cleared on successful completion.
    /// </summary>
    public const string GenaiHolocronCheckpoints = "genai.holocron_checkpoints";

    /// <summary>
    /// Per-batch progress for <c>HolocronProposalExtractorExecutor</c>. Survives mid-extraction
    /// process restarts (e.g. an API redeploy mid-run) so the resumed run skips already-processed
    /// batches and accumulated proposals are preserved.
    /// </summary>
    public const string GenaiHolocronProgress = "genai.holocron_progress";

    // ── Chat ──
    public const string ChatSessions = "chat.sessions";
    public const string UserSettings = "chat.user_settings";

    // ── Territory control ──
    public const string TerritorySnapshots = "territory.snapshots";
    public const string TerritoryYears = "territory.years";

    // ── Unified galaxy map ──
    public const string GalaxyYears = "galaxy.years";

    // ── Admin ──
    public const string JobToggles = "admin.job_toggles";

    /// <summary>
    /// One document per UTC day, populated by the nightly OpenAI spend sync.
    /// Read-only at runtime — the public /api/costs endpoint reads from here.
    /// Keyed by ISO-8601 date string (e.g. "2026-04-27"); rolling 90-day window.
    /// </summary>
    public const string SpendDaily = "admin.spend_daily";

    // ── Dynamically generated Ask page suggestions ──
    public const string SuggestionsExamples = "suggestions.examples";
}
