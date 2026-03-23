using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Processes each discovered page individually with a small LLM call,
/// extracting timeline events. Accumulates all events in shared state.
/// Each page gets its own bounded context — no context window overflow.
/// </summary>
internal sealed class EventExtractionExecutor : Executor<string, string>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Checkpoint-resumable state: saved at superstep boundaries, restored on rehydration
    private HashSet<int> _processedPageIds = [];
    private List<ExtractedEvent> _accumulatedEvents = [];

    public EventExtractionExecutor(
        IChatClient chatClient,
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId)
        : base("EventExtraction")
    {
        _chatClient = chatClient;
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    protected override ValueTask OnCheckpointingAsync(
        IWorkflowContext context, CancellationToken cancellationToken)
    {
        return context.QueueStateUpdateAsync("checkpoint",
            new ExtractionCheckpoint(_processedPageIds.ToList(), _accumulatedEvents),
            "Extraction", cancellationToken);
    }

    protected override async ValueTask OnCheckpointRestoredAsync(
        IWorkflowContext context, CancellationToken cancellationToken)
    {
        var checkpoint = await context.ReadStateAsync<ExtractionCheckpoint>(
            "checkpoint", "Extraction", cancellationToken);

        if (checkpoint is not null)
        {
            _processedPageIds = [.. checkpoint.ProcessedPageIds];
            _accumulatedEvents = [.. checkpoint.Events];
            _logger.LogInformation(
                "Restored extraction checkpoint: {ProcessedCount} pages processed, {EventCount} events accumulated",
                _processedPageIds.Count, _accumulatedEvents.Count);
        }
    }

    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var pages = await context.ReadStateAsync<List<PageContent>>("pages", "Discovery", ct)
            ?? throw new InvalidOperationException("No pages found in Discovery state");

        var characterTitle = await context.ReadStateAsync<string>("characterTitle", "Discovery", ct)
            ?? "Unknown";

        var remaining = pages.Where(p => !_processedPageIds.Contains(p.PageId)).ToList();

        if (_processedPageIds.Count > 0)
        {
            _logger.LogInformation(
                "Resuming extraction for {Title}: {Remaining}/{Total} pages remaining ({EventCount} events already accumulated)",
                characterTitle, remaining.Count, pages.Count, _accumulatedEvents.Count);
        }

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Extracting,
            $"Starting extraction from {remaining.Count} pages...",
            currentStep: _processedPageIds.Count, totalSteps: pages.Count,
            currentItem: remaining.FirstOrDefault()?.Title,
            eventsExtracted: _accumulatedEvents.Count);

        _logger.LogInformation("Starting per-page extraction for {Title}: {Count} pages",
            characterTitle, remaining.Count);

        var processed = _processedPageIds.Count;

        foreach (var page in remaining)
        {
            ct.ThrowIfCancellationRequested();

            _tracker?.UpdateProgress(_characterPageId, GenerationStage.Extracting,
                $"Extracting events from \"{page.Title}\" ({processed + 1}/{pages.Count})...",
                currentStep: processed, totalSteps: pages.Count,
                currentItem: page.Title,
                eventsExtracted: _accumulatedEvents.Count);

            try
            {
                var events = await ExtractEventsFromPage(page, characterTitle, ct);
                _accumulatedEvents.AddRange(events);
                _processedPageIds.Add(page.PageId);
                processed++;

                _logger.LogInformation(
                    "Extracted {EventCount} events from {PageTitle} ({Processed}/{Total})",
                    events.Count, page.Title, processed, pages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract events from {PageTitle}, skipping", page.Title);
                _processedPageIds.Add(page.PageId);
            }
        }

        // Store all extracted events in shared state for the review executor
        await context.QueueStateUpdateAsync("events", _accumulatedEvents, "Extraction", ct);

        _logger.LogInformation("Extraction complete for {Title}: {EventCount} events from {PageCount} pages",
            characterTitle, _accumulatedEvents.Count, processed);

        return $"Extracted {_accumulatedEvents.Count} events from {processed} pages for {characterTitle}";
    }

    private async Task<List<ExtractedEvent>> ExtractEventsFromPage(
        PageContent page, string characterTitle, CancellationToken ct)
    {
        var prompt = $"""
            Extract timeline events for the character "{characterTitle}" from this wiki page.
            Only extract events that directly involve or significantly affect {characterTitle}.
            If this page contains no relevant events for {characterTitle}, return an empty events array.

            Source Page: {page.Title}
            Template: {page.Template ?? "unknown"}

            Infobox Data:
            {page.InfoboxText}

            Article Content:
            {page.ContentSnippet}

            Return structured events with: eventType, description, year (float), demarcation (BBY/ABY),
            dateDescription, location, relatedCharacters (excluding {characterTitle}).

            Event types: Birth, Death, Battle, Duel, War, Marriage, Apprenticeship, Promotion,
            Exile, Capture, Rescue, Discovery, Betrayal, Alliance, Training, Mission,
            Transformation, Founding, Destruction, Other.
            """;

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<PageExtractionSchema>(
                schemaName: "page_events",
                schemaDescription: "Events extracted from a single wiki page"),
        };

        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            chatOptions,
            ct);

        var text = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Strip markdown fences if present
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```");
            if (firstNewline > 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }

        var schema = JsonSerializer.Deserialize<PageExtractionSchema>(text, JsonOptions);
        if (schema?.Events is null || schema.Events.Count == 0)
            return [];

        return schema.Events.Select(e => new ExtractedEvent(
            e.EventType ?? "Other",
            e.Description ?? "",
            e.Year,
            e.Demarcation,
            e.DateDescription,
            e.Location,
            e.RelatedCharacters ?? [],
            page.Title,
            page.WikiUrl
        )).ToList();
    }
}

/// <summary>
/// An event extracted from a single page, with source attribution.
/// Stored in workflow state for the review executor.
/// </summary>
internal sealed record ExtractedEvent(
    string EventType,
    string Description,
    float? Year,
    string? Demarcation,
    string? DateDescription,
    string? Location,
    List<string> RelatedCharacters,
    string SourcePageTitle,
    string SourceWikiUrl);

/// <summary>
/// Checkpoint state for resuming extraction via framework rehydration.
/// </summary>
internal sealed record ExtractionCheckpoint(
    List<int> ProcessedPageIds,
    List<ExtractedEvent> Events);
