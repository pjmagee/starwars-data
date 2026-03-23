using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Executors;

/// <summary>
/// Reviews all accumulated events from the extraction stage.
/// Deduplicates, fixes chronology, validates dates, and ensures consistency.
/// Single LLM call — input is only event JSON (bounded), not raw wiki content.
/// </summary>
internal sealed class EventReviewExecutor : Executor<string, string>
{
    private readonly IChatClient _chatClient;
    private readonly ILogger _logger;
    private readonly CharacterTimelineTracker? _tracker;
    private readonly int _characterPageId;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public EventReviewExecutor(
        IChatClient chatClient,
        ILogger logger,
        CharacterTimelineTracker? tracker,
        int characterPageId)
        : base("EventReview")
    {
        _chatClient = chatClient;
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var events = await context.ReadStateAsync<List<ExtractedEvent>>("events", "Extraction", ct)
            ?? throw new InvalidOperationException("No events found in Extraction state");

        var characterTitle = await context.ReadStateAsync<string>("characterTitle", "Discovery", ct)
            ?? "Unknown";

        _tracker?.UpdateProgress(_characterPageId, GenerationStage.Reviewing,
            $"AI is reviewing and deduplicating {events.Count} events...",
            currentStep: 0, totalSteps: 1,
            currentItem: characterTitle, eventsExtracted: events.Count);

        _logger.LogInformation("Reviewing {EventCount} events for {Title}", events.Count, characterTitle);

        if (events.Count == 0)
            return "{}";

        // Serialize events for the review prompt
        var eventsJson = JsonSerializer.Serialize(events, JsonOptions);

        var prompt = $"""
            Review and consolidate these {events.Count} timeline events for "{characterTitle}".

            TASKS:
            1. Remove duplicate events (same event described from multiple source pages — keep the most detailed version)
            2. Fix chronological inconsistencies in years/demarcation (BBY/ABY)
            3. Validate dates against known Star Wars timeline anchors (Battle of Yavin = 0 BBY/ABY)
            4. Merge events that describe the same occurrence but from different sources
            5. Ensure every event has a sourcePageTitle and sourceWikiUrl from the original extraction
            6. Keep all unique events — do not discard events just to shorten the list

            EVENTS:
            {eventsJson}
            """;

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<TimelineResponseSchema>(
                schemaName: "character_timeline",
                schemaDescription: "Reviewed and consolidated timeline events for a Star Wars character"),
        };

        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            chatOptions,
            ct);

        var text = response.Text?.Trim() ?? "";

        _logger.LogInformation("Review complete for {Title}: response length {Length}",
            characterTitle, text.Length);

        return text;
    }
}
