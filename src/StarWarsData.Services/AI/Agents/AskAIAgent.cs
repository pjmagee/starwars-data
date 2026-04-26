using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.Models;

namespace StarWarsData.Services.AI.Agents;

/// <summary>
/// The streaming chat agent served at <c>/kernel/stream</c> via the AGUI protocol.
/// Owns the construction of the <see cref="AIAgent"/> instance: chat client wiring,
/// toolkit registration (Component, DataExplorer, GraphRAG, KGAnalytics, wiki search,
/// MCP read-only Mongo tools), tool-call budget, topic guardrail, and instruction prompt.
///
/// Resolved as a DI singleton in <see cref="StarWarsData.ApiService"/> Program.cs;
/// <c>AddSingleton&lt;AIAgent&gt;(sp =&gt; sp.GetRequiredService&lt;AskAIAgent&gt;().Build())</c>
/// hands the constructed agent to <c>MapAGUI</c>.
/// </summary>
public sealed class AskAIAgent(
    IOptions<SettingsOptions> settingsOptions,
    OpenAIClient openAiClient,
    IMongoClient mongoClient,
    ByokChatClient byokClient,
    GraphRAGToolkit graphRAG,
    KnowledgeGraphQueryService kgService,
    OpenAiStatusService aiStatus,
    ILoggerFactory loggerFactory,
    [Microsoft.Extensions.DependencyInjection.FromKeyedServices("mongodb-mcp")] McpClient? mcpClient = null
)
{
    public AIAgent Build()
    {
        var settings = settingsOptions.Value;

        var componentToolkit = new ComponentToolkit();
        var dataExplorer = new DataExplorerToolkit(mongoClient, settingsOptions);

        // Wiki text search — registered directly as a tool so the model sees it
        // (UseAIContextProviders does not reliably surface tools via AGUI streaming)
        var pagesCollection = mongoClient.GetDatabase(settings.DatabaseName).GetCollection<BsonDocument>(Collections.Pages);
        var wikiSearchProvider = new StarWarsWikiSearchProvider(pagesCollection, loggerFactory);

        var kgAnalytics = new KGAnalyticsToolkit(kgService, mongoClient, settings.DatabaseName);

        var tools = new List<AITool>();
        tools.AddRange(componentToolkit.AsAIFunctions());
        tools.AddRange(dataExplorer.AsAIFunctions());
        tools.AddRange(graphRAG.AsAIFunctions());
        tools.AddRange(kgAnalytics.AsAIFunctions());
        tools.Add(
            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => wikiSearchProvider.SearchAsync(query, ct),
                "keyword_search",
                """
                Keyword search over wiki page titles and content. Fast, no AI cost.
                Best for exact name lookups. For why/how/explain questions, use semantic_search instead.
                """
            )
        );

        if (mcpClient is not null)
        {
            var allowedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "find", "aggregate", "count" };
            var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
            tools.AddRange(mcpTools.Select(t => t.WithName(t.Name.Replace('-', '_'))).Where(t => allowedMcpTools.Contains(t.Name)).Cast<AITool>());
        }

        var instructions = AgentPrompt.GetInstructions(settings.DatabaseName);

        // Explicit function-invocation wiring with a hard iteration cap. Default is 40, which
        // is far too generous for this app — see eng/design/012-ai-agent-tool-call-efficiency.md.
        // We also opt out of the agent's auto-wrapping (UseProvidedChatClientAsIs = true) so the
        // settings configured here are the ones that actually run.
        var chatClient = new ChatClientBuilder(byokClient)
            .UseFunctionInvocation(configure: c =>
            {
                c.MaximumIterationsPerRequest = 12;
                c.AllowConcurrentInvocation = true;
            })
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();

        // Lightweight classifier client for topic guardrail (always uses server key)
        var classifierClient = new ChatClientBuilder(openAiClient.GetResponsesClient().AsIChatClient("gpt-5.4-mini")).UseOpenTelemetry(configure: t => t.EnableSensitiveData = true).Build();

        var guardrailLogger = loggerFactory.CreateLogger("StarWarsTopicGuardrail");
        var budgetLogger = loggerFactory.CreateLogger("ToolCallBudget");

        var agentOptions = new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools },
            UseProvidedChatClientAsIs = true,
        };

        return chatClient
            .AsAIAgent(agentOptions)
            .AsBuilder()
            .UseToolCallBudget(softWarnAt: 10, hardLimit: 15, logger: budgetLogger)
            .UseStarWarsTopicGuardrail(classifierClient, aiStatus, guardrailLogger)
            .Build();
    }
}
