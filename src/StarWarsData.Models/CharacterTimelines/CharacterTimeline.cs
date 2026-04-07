using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Queries;

namespace StarWarsData.Models.Entities;

/// <summary>
/// AI-generated timeline for a single character, produced by feeding
/// the character's page and all linked pages into an LLM.
/// Stored in the genai.character_timelines collection.
/// </summary>
public class CharacterTimeline
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    [BsonElement("characterPageId")]
    public int CharacterPageId { get; set; }

    [BsonElement("characterTitle")]
    public string CharacterTitle { get; set; } = string.Empty;

    [BsonElement("characterWikiUrl")]
    public string CharacterWikiUrl { get; set; } = string.Empty;

    [BsonElement("imageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("continuity")]
    [BsonRepresentation(BsonType.String)]
    public Continuity Continuity { get; set; } = Continuity.Unknown;

    [BsonElement("events")]
    public List<CharacterEvent> Events { get; set; } = [];

    [BsonElement("sources")]
    public List<SourcePage> Sources { get; set; } = [];

    [BsonElement("generatedAt")]
    public DateTime GeneratedAt { get; set; }

    [BsonElement("modelUsed")]
    public string ModelUsed { get; set; } = string.Empty;
}

/// <summary>
/// A single event in a character's life, extracted by the LLM from wiki pages and enriched
/// with temporal anchors derived from the knowledge graph.
/// </summary>
public class CharacterEvent : IComparable<CharacterEvent>
{
    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>One-sentence headline used for the sortable event title.</summary>
    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 2–4 sentences of narrative describing what happened, the context, and the consequences
    /// for the character. Fills in the body text when the headline is too terse to be informative.
    /// </summary>
    [BsonElement("narrative")]
    [BsonIgnoreIfNull]
    public string? Narrative { get; set; }

    /// <summary>Why this event matters in the character's arc (optional, 1 sentence).</summary>
    [BsonElement("significance")]
    [BsonIgnoreIfNull]
    public string? Significance { get; set; }

    /// <summary>What happened immediately before that set this event up (optional, 1 sentence).</summary>
    [BsonElement("precedingContext")]
    [BsonIgnoreIfNull]
    public string? PrecedingContext { get; set; }

    /// <summary>Consequences or outcomes that followed from this event (0..n bullet strings).</summary>
    [BsonElement("consequences")]
    public List<string> Consequences { get; set; } = [];

    [BsonElement("year")]
    public float? Year { get; set; }

    [BsonElement("demarcation")]
    [BsonRepresentation(BsonType.String)]
    public Demarcation Demarcation { get; set; } = Demarcation.Unset;

    /// <summary>
    /// Sort-hint year inferred from the knowledge graph when the LLM couldn't assign one.
    /// Never overwrites <see cref="Year"/>; used only as a tiebreak in <see cref="CompareTo"/>.
    /// </summary>
    [BsonElement("inferredYear")]
    [BsonIgnoreIfNull]
    public int? InferredYear { get; set; }

    /// <summary>Demarcation accompanying <see cref="InferredYear"/>.</summary>
    [BsonElement("inferredDemarcation")]
    [BsonIgnoreIfDefault]
    public Demarcation InferredDemarcation { get; set; } = Demarcation.Unset;

    /// <summary>Provenance string for InferredYear, e.g. "kg-edge:apprenticed_to→Qui-Gon Jinn".</summary>
    [BsonElement("yearSource")]
    [BsonIgnoreIfNull]
    public string? YearSource { get; set; }

    [BsonElement("dateDescription")]
    public string? DateDescription { get; set; }

    [BsonElement("location")]
    public string? Location { get; set; }

    [BsonElement("relatedCharacters")]
    public List<string> RelatedCharacters { get; set; } = [];

    [BsonElement("sourcePageTitle")]
    public string? SourcePageTitle { get; set; }

    [BsonElement("sourceWikiUrl")]
    public string? SourceWikiUrl { get; set; }

    /// <summary>
    /// Derive a sort key that collapses (Year, Demarcation) into a signed galactic year
    /// (negative = BBY, positive = ABY), falling back to InferredYear when the LLM year is null.
    /// Returns null only if both sources are null.
    /// </summary>
    public int? SortKey()
    {
        var (year, dem) = (Year, Demarcation) switch
        {
            (not null, Demarcation.Bby) => ((float?)Year, Demarcation.Bby),
            (not null, Demarcation.Aby) => ((float?)Year, Demarcation.Aby),
            (not null, _) => ((float?)Year, Demarcation.Aby), // treat Unset as ABY fallback
            _ => (InferredYear.HasValue ? (float?)InferredYear : null, InferredDemarcation),
        };
        if (year is null)
            return null;
        return dem == Demarcation.Bby ? -(int)year.Value : (int)year.Value;
    }

    public int CompareTo(CharacterEvent? other)
    {
        if (other is null)
            return 1;

        var a = SortKey();
        var b = other.SortKey();

        if (a is null && b is null)
            return 0;
        if (a is null)
            return 1;
        if (b is null)
            return -1;

        return a.Value.CompareTo(b.Value);
    }
}

/// <summary>
/// Paginated list of character timeline summaries.
/// </summary>
public class CharacterTimelineListResult
{
    public List<CharacterTimelineSummary> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>
/// Summary projection for listing character timelines (no full event list).
/// </summary>
public class CharacterTimelineSummary
{
    [BsonElement("characterPageId")]
    public int CharacterPageId { get; set; }

    [BsonElement("characterTitle")]
    public string CharacterTitle { get; set; } = string.Empty;

    [BsonElement("imageUrl")]
    public string? ImageUrl { get; set; }

    [BsonElement("continuity")]
    public Continuity Continuity { get; set; }

    [BsonElement("eventCount")]
    public int EventCount { get; set; }

    [BsonElement("sourceCount")]
    public int SourceCount { get; set; }

    [BsonElement("generatedAt")]
    public DateTime GeneratedAt { get; set; }
}

/// <summary>
/// A wiki page that was used as source material for generating a character timeline.
/// </summary>
public class SourcePage
{
    [BsonElement("pageId")]
    public int PageId { get; set; }

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("wikiUrl")]
    public string WikiUrl { get; set; } = string.Empty;
}

/// <summary>
/// Character search result showing a character page and whether a timeline exists.
/// </summary>
public class CharacterSearchResult
{
    public int PageId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string WikiUrl { get; set; } = string.Empty;
    public Continuity Continuity { get; set; }
    public bool HasTimeline { get; set; }
    public GenerationStatus? GenerationStatus { get; set; }
}

public enum GenerationStage
{
    Queued,
    Discovering,
    Bundling,
    Extracting,
    Consolidating,
    Reviewing,
    Saving,
    Complete,
    Failed,
}

public class GenerationStatus
{
    public GenerationStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string? Error { get; set; }
    public string? CharacterTitle { get; set; }

    // Granular progress within the current stage
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? CurrentItem { get; set; }
    public int EventsExtracted { get; set; }

    /// <summary>
    /// True if there are pending checkpoints from an interrupted generation.
    /// The frontend can use this to offer a "Resume" option.
    /// </summary>
    public bool HasPendingCheckpoint { get; set; }

    /// <summary>
    /// Live activity log populated from custom workflow events.
    /// </summary>
    public List<ActivityLogEntry> ActivityLog { get; set; } = [];
}

/// <summary>
/// A single entry in the generation activity log, surfacing workflow events to the frontend.
/// </summary>
public class ActivityLogEntry
{
    public DateTime Timestamp { get; set; }

    /// <summary>Discovery, Extraction, Review, System</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Discriminator: page_discovered, extraction_started, event_extracted,
    /// extraction_empty, extraction_failed, review_complete, etc.
    /// </summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>Human-readable summary shown in the activity feed.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Optional structured detail (serialized as JSON for the frontend).</summary>
    public object? Detail { get; set; }
}
