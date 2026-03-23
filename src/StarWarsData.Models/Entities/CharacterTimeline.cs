using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using StarWarsData.Models.Queries;

namespace StarWarsData.Models.Entities;

/// <summary>
/// AI-generated timeline for a single character, produced by feeding
/// the character's page and all linked pages into an LLM.
/// Stored in the starwars-character-timelines database.
/// </summary>
public class CharacterTimeline
{
    [BsonId]
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
/// A single event in a character's life, extracted by the LLM.
/// </summary>
public class CharacterEvent : IComparable<CharacterEvent>
{
    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    [BsonElement("year")]
    public float? Year { get; set; }

    [BsonElement("demarcation")]
    [BsonRepresentation(BsonType.String)]
    public Demarcation Demarcation { get; set; } = Demarcation.Unset;

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

    public int CompareTo(CharacterEvent? other)
    {
        if (other is null) return 1;
        if (!Year.HasValue && !other.Year.HasValue) return 0;
        if (!Year.HasValue) return 1;
        if (!other.Year.HasValue) return -1;

        switch (Demarcation)
        {
            case Demarcation.Bby when other.Demarcation == Demarcation.Aby:
                return -1;
            case Demarcation.Aby when other.Demarcation == Demarcation.Bby:
                return 1;
        }

        return Demarcation switch
        {
            Demarcation.Bby when other.Demarcation == Demarcation.Bby =>
                other.Year.Value.CompareTo(Year.Value),
            _ => Year.Value.CompareTo(other.Year.Value),
        };
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
    public int CharacterPageId { get; set; }
    public string CharacterTitle { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public Continuity Continuity { get; set; }
    public int EventCount { get; set; }
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
    Extracting,
    Reviewing,
    Saving,
    Complete,
    Failed
}

public class GenerationStatus
{
    public GenerationStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string? Error { get; set; }

    // Granular progress within the current stage
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? CurrentItem { get; set; }
    public int EventsExtracted { get; set; }
}
