using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.Services;

namespace StarWarsData.AgentTests.Infrastructure;

/// <summary>
/// Shared test fixture that replicates the production AI agent setup.
/// Connects to starwars-dev MongoDB and uses real OpenAI API.
/// Initialized once per test assembly via [AssemblyInitialize].
/// </summary>
[TestClass]
public class AgentFixture
{
    public static IMongoClient MongoClient { get; private set; } = null!;
    public static IChatClient EvaluatorClient { get; private set; } = null!;
    public static IList<AITool> Tools { get; private set; } = null!;
    public static GraphRAGToolkit GraphRAG { get; private set; } = null!;
    public static ComponentToolkit Components { get; private set; } = null!;

    static OpenAIClient OpenAiClient { get; set; } = null!;

    const string DefaultDatabaseName = "starwars-dev";

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        var apiKey = Environment.GetEnvironmentVariable("STARWARS_OPENAI_KEY") ?? throw new InvalidOperationException("STARWARS_OPENAI_KEY environment variable is required");

        var mongoConnectionString =
            Environment.GetEnvironmentVariable("STARWARS_MONGO_CONNECTION")
            ?? throw new InvalidOperationException("STARWARS_MONGO_CONNECTION environment variable is required (e.g. mongodb://user:pass@host:port/?authSource=admin)");

        var databaseName = Environment.GetEnvironmentVariable("STARWARS_MONGO_DATABASE") ?? DefaultDatabaseName;

        OpenAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) });

        MongoClient = new MongoClient(mongoConnectionString);
        var settings = Options.Create(new SettingsOptions { DatabaseName = databaseName, HangfireDb = $"{databaseName}-hangfire" });

        // Build toolkits — same as Program.cs
        var kgService = new KnowledgeGraphQueryService(MongoClient, settings);
        var embedder = new NoOpEmbeddingGenerator();
        var searchLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<SemanticSearchService>.Instance;
        var semanticSearch = new SemanticSearchService(MongoClient, settings, embedder, searchLogger);
        GraphRAG = new GraphRAGToolkit(kgService, semanticSearch, MongoClient, databaseName);
        Components = new ComponentToolkit();
        var dataExplorer = new DataExplorerToolkit(MongoClient, settings);

        var pagesCollection = MongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>(Collections.Pages);
        var wikiSearchProvider = new StarWarsWikiSearchProvider(pagesCollection, NullLoggerFactory.Instance);

        // Assemble tools — same order as production
        var tools = new List<AITool>();
        tools.AddRange(Components.AsAIFunctions());
        tools.AddRange(dataExplorer.AsAIFunctions());
        tools.AddRange(GraphRAG.AsAIFunctions());
        tools.Add(
            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => wikiSearchProvider.SearchAsync(query, ct),
                "keyword_search",
                "Keyword search over wiki page titles and content. For conceptual questions use semantic_search."
            )
        );
        Tools = tools;

        EvaluatorClient = new ChatClientBuilder(OpenAiClient.GetChatClient("gpt-4o-mini").AsIChatClient()).Build();
    }

    /// <summary>
    /// Run a prompt through the agent with function invocation and capture tool calls.
    /// Creates a fresh chat client per call (captures are per-invocation) but reuses the shared OpenAI client.
    /// </summary>
    public static async Task<ChatResponse> RunPrompt(string userMessage, ConversationCapture capture, string? continuityPrefix = null)
    {
        var chatClient = new ChatClientBuilder(OpenAiClient.GetChatClient("gpt-4o-mini").AsIChatClient()).UseFunctionInvocation().Use(capture.CreateMiddleware()).Build();

        var prefix = continuityPrefix ?? "[CONTINUITY: Both] [PREFER: auto] ";

        var messages = new List<ChatMessage> { new(ChatRole.System, AgentPrompt.Instructions), new(ChatRole.User, prefix + userMessage) };

        var options = new ChatOptions { Tools = Tools };

        return await chatClient.GetResponseAsync(messages, options);
    }
}

/// <summary>
/// No-op embedding generator for tests that don't need vector search.
/// </summary>
internal sealed class NoOpEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("no-op");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(_ => new Embedding<float>(new float[] { 0f })).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
