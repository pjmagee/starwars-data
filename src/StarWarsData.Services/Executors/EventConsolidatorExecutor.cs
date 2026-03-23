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

    public EventConsolidatorExecutor(
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId)
        : base("EventConsolidation")
    {
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var events = await context.ReadStateAsync<List<ExtractedEvent>>("events", "BatchExtraction", ct)
            ?? throw new InvalidOperationException("No events found in BatchExtraction state");

        var characterTitle = await context.ReadStateAsync<string>("characterTitle", "Discovery", ct)
            ?? "Unknown";

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Consolidating,
            $"Consolidating {events.Count} extracted events...",
            currentStep: 0, totalSteps: 1,
            currentItem: characterTitle, eventsExtracted: events.Count);

        _logger.LogInformation("Consolidating {EventCount} events for {Title}", events.Count, characterTitle);

        // Lightweight deduplication: remove exact description duplicates, keeping the one with more data
        var deduplicated = events
            .GroupBy(e => e.Description.Trim().ToLowerInvariant())
            .Select(g => g
                .OrderByDescending(e => ScoreCompleteness(e))
                .First())
            .ToList();

        var removed = events.Count - deduplicated.Count;

        // Store consolidated events for the review executor
        await context.QueueStateUpdateAsync("events", deduplicated, "Consolidation", ct);

        await context.AddEventAsync(new ConsolidationCompleteEvent(new ConsolidationCompleteData(
            events.Count, deduplicated.Count, removed)), ct);

        _logger.LogInformation(
            "Consolidation complete for {Title}: {InputCount} → {OutputCount} events ({Removed} exact duplicates removed)",
            characterTitle, events.Count, deduplicated.Count, removed);

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Consolidating,
            $"Consolidated {events.Count} → {deduplicated.Count} events ({removed} duplicates removed)",
            currentStep: 1, totalSteps: 1,
            currentItem: characterTitle, eventsExtracted: deduplicated.Count);

        return $"Consolidated {events.Count} → {deduplicated.Count} events for {characterTitle}";
    }

    /// <summary>
    /// Score how complete an event record is — prefer events with more filled fields.
    /// </summary>
    private static int ScoreCompleteness(ExtractedEvent e)
    {
        var score = 0;
        if (e.Year.HasValue) score += 2;
        if (!string.IsNullOrWhiteSpace(e.Demarcation)) score++;
        if (!string.IsNullOrWhiteSpace(e.DateDescription)) score++;
        if (!string.IsNullOrWhiteSpace(e.Location)) score++;
        if (e.RelatedCharacters.Count > 0) score++;
        if (!string.IsNullOrWhiteSpace(e.SourcePageTitle)) score++;
        if (e.Description.Length > 50) score++; // prefer more detailed descriptions
        return score;
    }
}
