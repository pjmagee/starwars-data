using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Pure C# executor (no LLM) that consolidates events from batch extraction.
/// Performs lightweight deduplication (exact description matches) and counts.
/// Heavy deduplication (semantic) is left to the review executor.
///
/// Superstep 4: Discovery → Bundler → Extraction → [Consolidation] → Review
/// </summary>
internal sealed class EventConsolidatorExecutor : Executor<string, string>
{
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;

    public EventConsolidatorExecutor(ILogger logger, CharacterTimelineTracker? tracker, int characterPageId)
        : base("EventConsolidation")
    {
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var events = await context.ReadStateAsync<List<ExtractedEvent>>("events", "BatchExtraction", ct) ?? throw new InvalidOperationException("No events found in BatchExtraction state");

        var characterTitle = await context.ReadStateAsync<string>("characterTitle", "Discovery", ct) ?? "Unknown";

        _tracker?.UpdateProgress(
            _characterPageId,
            GenerationStage.Consolidating,
            $"Consolidating {events.Count} extracted events...",
            currentStep: 0,
            totalSteps: 1,
            currentItem: characterTitle,
            eventsExtracted: events.Count
        );

        _logger.LogInformation("Consolidating {EventCount} events for {Title}", events.Count, characterTitle);

        // Tight deduplication key: events are considered duplicates when they share
        // (eventType, bucketed year, normalised location). This catches the common case
        // where two batches extracted the same battle/mission with slightly different wording,
        // which the old "exact description string" key never matched.
        var deduplicated = events.GroupBy(e => DedupKey(e)).Select(g => g.OrderByDescending(ScoreCompleteness).First()).ToList();

        var removed = events.Count - deduplicated.Count;

        // Store consolidated events for the review executor
        await context.QueueStateUpdateAsync("events", deduplicated, "Consolidation", ct);

        await context.AddEventAsync(new ConsolidationCompleteEvent(new ConsolidationCompleteData(events.Count, deduplicated.Count, removed)), ct);

        _logger.LogInformation(
            "Consolidation complete for {Title}: {InputCount} → {OutputCount} events ({Removed} exact duplicates removed)",
            characterTitle,
            events.Count,
            deduplicated.Count,
            removed
        );

        _tracker?.UpdateProgress(
            _characterPageId,
            GenerationStage.Consolidating,
            $"Consolidated {events.Count} → {deduplicated.Count} events ({removed} duplicates removed)",
            currentStep: 1,
            totalSteps: 1,
            currentItem: characterTitle,
            eventsExtracted: deduplicated.Count
        );

        return $"Consolidated {events.Count} → {deduplicated.Count} events for {characterTitle}";
    }

    /// <summary>
    /// Build a semantic dedup key: (eventType, year-bucket, normalised-location).
    /// Year bucket is the rounded integer year with demarcation, so "22 BBY" and "22.5 BBY" collide.
    /// Location is lower-cased and stripped of leading articles.
    /// For undated events, we fall back to a trimmed description prefix so at least exact repeats collapse.
    /// </summary>
    private static string DedupKey(ExtractedEvent e)
    {
        var typeKey = (e.EventType ?? "Other").Trim().ToLowerInvariant();
        var location = NormalizeLocation(e.Location);

        if (e.Year.HasValue)
        {
            var yearKey = (int)Math.Round(e.Year.Value);
            var dem = (e.Demarcation ?? "").Trim().ToUpperInvariant();
            return $"{typeKey}|{yearKey}{dem}|{location}";
        }

        // Undated: fall back to normalised first 60 chars of description.
        var descKey = (e.Description ?? "").Trim().ToLowerInvariant();
        if (descKey.Length > 60)
            descKey = descKey[..60];
        return $"{typeKey}|undated|{location}|{descKey}";
    }

    private static string NormalizeLocation(string? loc)
    {
        if (string.IsNullOrWhiteSpace(loc))
            return "";
        var l = loc.Trim().ToLowerInvariant();
        if (l.StartsWith("the "))
            l = l[4..];
        if (l.StartsWith("planet "))
            l = l[7..];
        return l;
    }

    /// <summary>
    /// Score how complete an event record is — prefer events with more filled fields
    /// and richer narrative content.
    /// </summary>
    private static int ScoreCompleteness(ExtractedEvent e)
    {
        var score = 0;
        if (e.Year.HasValue)
            score += 2;
        if (!string.IsNullOrWhiteSpace(e.Demarcation))
            score++;
        if (!string.IsNullOrWhiteSpace(e.DateDescription))
            score++;
        if (!string.IsNullOrWhiteSpace(e.Location))
            score++;
        if (e.RelatedCharacters.Count > 0)
            score++;
        if (!string.IsNullOrWhiteSpace(e.SourcePageTitle))
            score++;
        if (e.Description.Length > 50)
            score++;
        // Narrative fields weigh heavily — they are why we rewrote the pipeline.
        if (!string.IsNullOrWhiteSpace(e.Narrative) && e.Narrative.Length > 100)
            score += 3;
        if (!string.IsNullOrWhiteSpace(e.Significance))
            score++;
        if (e.Consequences.Count > 0)
            score++;
        return score;
    }
}
