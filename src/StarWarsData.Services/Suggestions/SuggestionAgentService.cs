using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services.Suggestions;

/// <summary>
/// Uses an AI agent with KG tools to explore the knowledge graph and generate
/// dynamic Ask page example questions. Replaces the template-based
/// <c>SuggestionGenerator</c> — the agent discovers entities, relationships,
/// temporal patterns, and property values on its own and produces naturally
/// phrased, grammatically correct prompts grounded in real KG data.
///
/// Runs as a weekly Hangfire recurring job. The agent is given the full
/// GraphRAG + KGAnalytics toolkits (~30 tools) and asked to produce ~6
/// suggestions per (mode × continuity × realm) bucket (28 buckets total).
/// </summary>
public sealed class SuggestionAgentService(
    IMongoClient mongoClient,
    IOptions<SettingsOptions> options,
    KnowledgeGraphQueryService kgService,
    IChatClient chatClient,
    ILogger<SuggestionAgentService> logger
)
{
    readonly IMongoDatabase _db = mongoClient.GetDatabase(options.Value.DatabaseName);

    IMongoCollection<GeneratedSuggestion> Output => _db.GetCollection<GeneratedSuggestion>(Collections.SuggestionsExamples);

    static readonly string[] Modes = ["chart", "graph", "table", "data_table", "timeline", "infobox", "text"];

    static readonly (Continuity C, Realm R, string Label)[] Buckets =
    [
        (Continuity.Canon, Realm.Starwars, "Canon in-universe"),
        (Continuity.Legends, Realm.Starwars, "Legends in-universe"),
        (Continuity.Canon, Realm.Real, "Canon real-world/publication"),
        (Continuity.Legends, Realm.Real, "Legends real-world/publication"),
    ];

    const int SuggestionsPerBucket = 6;

    public async Task GenerateAsync(CancellationToken ct = default)
    {
        logger.LogInformation("SuggestionAgentService: starting weekly refresh…");

        // Build the toolkits — same ones the Ask agent uses, minus semantic_search
        // (requires an embedding generator not registered in Admin).
        var graphRAG = new GraphRAGToolkit(kgService, search: null!, mongoClient, options.Value.DatabaseName);

        var kgAnalytics = new KGAnalyticsToolkit(kgService, mongoClient, options.Value.DatabaseName);

        var tools = new List<AITool>();
        // Exclude semantic_search — it requires SemanticSearchService which isn't available in Admin.
        tools.AddRange(graphRAG.AsAIFunctions().Where(t => t.Name != "semantic_search"));
        tools.AddRange(kgAnalytics.AsAIFunctions());

        var all = new List<GeneratedSuggestion>();

        foreach (var (continuity, realm, bucketLabel) in Buckets)
        {
            logger.LogInformation("SuggestionAgentService: generating suggestions for {Bucket}…", bucketLabel);

            try
            {
                var suggestions = await GenerateBucketAsync(tools, continuity, realm, bucketLabel, ct);
                all.AddRange(suggestions);
                logger.LogInformation(
                    "SuggestionAgentService: {Bucket} produced {Count} suggestions across {Modes} modes.",
                    bucketLabel,
                    suggestions.Count,
                    suggestions.Select(s => s.Mode).Distinct().Count()
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SuggestionAgentService: failed to generate suggestions for {Bucket}; continuing with other buckets.", bucketLabel);
            }
        }

        // Dedup by prompt text.
        var deduped = all.GroupBy(s => s.Prompt, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        if (deduped.Count == 0)
        {
            logger.LogWarning("SuggestionAgentService: produced zero suggestions; leaving existing cache intact.");
            return;
        }

        // Replace-all.
        await Output.DeleteManyAsync(FilterDefinition<GeneratedSuggestion>.Empty, ct);
        await Output.InsertManyAsync(deduped, cancellationToken: ct);

        var summary = deduped
            .GroupBy(s => (s.Continuity, s.Realm, s.Mode))
            .OrderBy(g => g.Key.Continuity)
            .ThenBy(g => g.Key.Realm)
            .ThenBy(g => g.Key.Mode)
            .Select(g => $"{g.Key.Continuity}/{g.Key.Realm}/{g.Key.Mode}={g.Count()}");

        logger.LogInformation(
            "SuggestionAgentService: wrote {Count} suggestions across {Buckets} buckets.\n  {Summary}",
            deduped.Count,
            deduped.GroupBy(s => (s.Continuity, s.Realm, s.Mode)).Count(),
            string.Join(", ", summary)
        );
    }

    /// <summary>Schema for structured output — the model must produce exactly this shape.</summary>
    sealed record SuggestionBatch([property: JsonPropertyName("suggestions")] List<SuggestionItem> Suggestions);

    sealed record SuggestionItem([property: JsonPropertyName("mode")] string Mode, [property: JsonPropertyName("prompt")] string Prompt, [property: JsonPropertyName("entityIds")] List<int> EntityIds);

    async Task<List<GeneratedSuggestion>> GenerateBucketAsync(List<AITool> tools, Continuity continuity, Realm realm, string bucketLabel, CancellationToken ct)
    {
        // ── Phase 1: Explore the KG with tools ──
        // The agent calls KG tools to discover entities, relationships, properties, eras,
        // and temporal data. The conversation accumulates tool results as context.

        var explorationPrompt = BuildExplorationPrompt(continuity, realm, bucketLabel);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, explorationPrompt),
            new(
                ChatRole.User,
                $"Explore the knowledge graph for {bucketLabel} content. "
                    + "Discover a diverse set of entity types, notable entities, relationship paths, "
                    + "temporal patterns, and property values. I need enough material to write "
                    + $"{SuggestionsPerBucket} interesting questions for each of the 7 visualization modes."
            ),
        };

        var explorationOptions = new ChatOptions { Tools = tools, ToolMode = ChatToolMode.Auto };

        var explorationResponse = await chatClient.GetResponseAsync(messages, explorationOptions, ct);

        // Carry the full conversation forward — all tool calls + results are now context.
        messages.AddMessages(explorationResponse);

        // ── Phase 2: Generate suggestions with structured output (no tools) ──
        // The model now has rich KG context from phase 1. Ask it to produce the
        // suggestions using strict JSON schema — no tools, no free-form text.

        messages.Add(new(ChatRole.User, BuildFormattingPrompt(bucketLabel)));

        var formattingOptions = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema<SuggestionBatch>(schemaName: "suggestion_batch", schemaDescription: "A batch of example questions for the Ask page"),
        };

        var formattingResponse = await chatClient.GetResponseAsync(messages, formattingOptions, ct);

        return ParseResponse(formattingResponse, continuity, realm);
    }

    static string BuildExplorationPrompt(Continuity continuity, Realm realm, string bucketLabel) =>
        $$"""
            You are exploring a Star Wars knowledge graph to gather material for writing example questions.
            Your goal is to discover interesting entities, relationships, temporal patterns, and properties
            that can be turned into compelling questions for a Star Wars fan audience.

            Scope: **{{bucketLabel}}**
            - Continuity: **{{continuity}}** — {{(continuity == Continuity.Canon ? "use the continuity filter 'Canon' when calling tools" : "use the continuity filter 'Legends' when calling tools")}}
            - Realm: **{{realm}}** — {{(
                realm == Realm.Starwars
                    ? "in-universe content (characters, battles, planets, ships, organizations, species, etc.)"
                    : "real-world content (publications like novels, comics, audiobooks, games — their authors, release dates, production context)"
            )}}

            Explore broadly across many entity types. Don't just focus on characters. Discover:
            - What entity types exist and which have the most entries
            - Notable, highly-connected entities across different types
            - Interesting multi-hop relationship paths (A connected to B connected to C)
            - Properties and their common values (e.g. which species are most common)
            - Temporal data — eras, lifecycles, date ranges
            - Cross-type connections (characters to battles, organizations to planets, etc.)

            Summarize your findings when done — list the most interesting entities, paths, and patterns you found.
            """;

    static string BuildFormattingPrompt(string bucketLabel) =>
        $$"""
            Based on everything you discovered above, write exactly {{SuggestionsPerBucket}} example questions
            for EACH of these 7 modes ({{SuggestionsPerBucket * Modes.Length}} total):

            1. chart — aggregation/comparison (pie charts, bar charts, radar comparisons)
            2. graph — relationship networks, lineage trees, connection paths
            3. table — browsable lists with columns
            4. data_table — cross-referenced/joined data comparisons
            5. timeline — chronological events, lifecycles, temporal sequences
            6. infobox — entity profiles and side-by-side comparisons
            7. text — deep analysis, motivations, causes, lore research

            CRITICAL: Each prompt will be shown directly to users on a website. Write them as a curious
            Star Wars fan would naturally ask. No technical jargon, no IDs, no field names, no tool names,
            no implementation details. Just plain, natural questions that reference real entities you found.

            Good: "Which species are most common among members of the Jedi Order?"
            Bad: "Count nodes by property Species where affiliated_with Jedi Order (id: 12345)"

            Good: "Trace the master-apprentice chain from Yoda to Ahsoka Tano"
            Bad: "Show apprentice_of edges from Yoda (452100) using traverse_graph depth=3"

            Put PageIds only in the entityIds array, never in the prompt text.
            """;

    List<GeneratedSuggestion> ParseResponse(ChatResponse response, Continuity continuity, Realm realm)
    {
        var results = new List<GeneratedSuggestion>();

        var text = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            var finishReason = response.FinishReason?.ToString() ?? "null";
            logger.LogWarning("SuggestionAgentService: empty response from formatting phase. FinishReason={FinishReason}", finishReason);
            return results;
        }

        try
        {
            var batch = JsonSerializer.Deserialize<SuggestionBatch>(text, JsonOpts);
            if (batch?.Suggestions is null)
                return results;

            foreach (var item in batch.Suggestions)
            {
                if (string.IsNullOrWhiteSpace(item.Mode) || string.IsNullOrWhiteSpace(item.Prompt))
                    continue;

                var mode = item.Mode.Trim().ToLowerInvariant();
                if (!Modes.Contains(mode))
                {
                    logger.LogDebug("SuggestionAgentService: skipping suggestion with unknown mode '{Mode}'.", mode);
                    continue;
                }

                results.Add(
                    new GeneratedSuggestion
                    {
                        Id = ObjectId.GenerateNewId().ToString(),
                        Mode = mode,
                        Shape = $"agent.{mode}",
                        Prompt = item.Prompt.Trim(),
                        Continuity = continuity,
                        Realm = realm,
                        EntityIds = item.EntityIds ?? [],
                        GeneratedAt = DateTime.UtcNow,
                    }
                );
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "SuggestionAgentService: failed to deserialize structured response. Text: {Text}", text[..Math.Min(200, text.Length)]);
        }

        return results;
    }

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
