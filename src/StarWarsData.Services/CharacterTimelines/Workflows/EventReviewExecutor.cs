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

    public EventReviewExecutor(IChatClient chatClient, ILogger logger, CharacterTimelineTracker? tracker, int characterPageId)
        : base("EventReview")
    {
        _chatClient = chatClient;
        _logger = logger;
        _tracker = tracker;
        _characterPageId = characterPageId;
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken ct = default)
    {
        var events = await context.ReadStateAsync<List<ExtractedEvent>>("events", "Consolidation", ct) ?? throw new InvalidOperationException("No events found in Consolidation state");

        var characterTitle = await context.ReadStateAsync<string>("characterTitle", "Discovery", ct) ?? "Unknown";

        var characterContinuity = await context.ReadStateAsync<string>("characterContinuity", "Discovery", ct) ?? "Unknown";

        _tracker?.UpdateProgress(
            _characterPageId,
            GenerationStage.Reviewing,
            $"AI is reviewing and deduplicating {events.Count} events...",
            currentStep: 0,
            totalSteps: 1,
            currentItem: characterTitle,
            eventsExtracted: events.Count
        );

        _logger.LogInformation("Reviewing {EventCount} events for {Title}", events.Count, characterTitle);

        if (events.Count == 0)
            return "{}";

        // Serialize events for the review prompt
        var eventsJson = JsonSerializer.Serialize(events, JsonOptions);

        var prompt = $"""
            Review and consolidate these {events.Count} timeline events for "{characterTitle}".
            This character belongs to the {characterContinuity} continuity of Star Wars.

            TASKS:
            1. Remove duplicate events (same event described from multiple source pages — keep the one with the
               richest narrative, significance and consequences).
            2. Fix chronological inconsistencies in year/demarcation (BBY/ABY). Use the Battle of Yavin anchor
               (0 BBY/ABY). Do NOT change dates that came from the knowledge graph anchors.
            3. Merge events that describe the same occurrence — combine their narratives so the result is
               MORE detailed, not less.
            4. Preserve every non-empty narrative, significance, precedingContext, and consequences field. Do
               NOT drop these fields; if you find an event with a thin narrative, extend it using any other
               source events that describe the same occurrence.
            5. Ensure every event has a sourcePageTitle and sourceWikiUrl.
            6. Keep all unique events — do not discard events just to shorten the list.
            7. Remove any events that reference content exclusive to a different continuity.
            8. If an event's description is a single short sentence but its narrative is empty, try to
               synthesise a narrative from the description + relatedCharacters + location. Thin output is
               the single biggest quality problem — fix it here.

            EVENTS:
            {eventsJson}
            """;

        var chatOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<TimelineResponseSchema>(
                schemaName: "character_timeline",
                schemaDescription: "Reviewed and consolidated timeline events for a Star Wars character"
            ),
        };

        var response = await _chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], chatOptions, ct);

        var text = response.Text?.Trim() ?? "";

        // Try to parse to get output count for the review summary event
        int outputCount = 0;
        try
        {
            var parsed = JsonSerializer.Deserialize<TimelineResponseSchema>(text.StartsWith("```") ? StripMarkdownFences(text) : text, JsonOptions);
            outputCount = parsed?.Events?.Count ?? 0;
        }
        catch
        { /* best-effort count */
        }

        await context.AddEventAsync(new ReviewCompleteEvent(new ReviewCompleteData(events.Count, outputCount, events.Count - outputCount)), ct);

        _logger.LogInformation("Review complete for {Title}: response length {Length}", characterTitle, text.Length);

        return text;
    }

    private static string StripMarkdownFences(string text)
    {
        var firstNewline = text.IndexOf('\n');
        var lastFence = text.LastIndexOf("```");
        if (firstNewline > 0 && lastFence > firstNewline)
            return text[(firstNewline + 1)..lastFence].Trim();
        return text;
    }
}
