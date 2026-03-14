using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.OpenAI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using StarWarsData.Models.Queries;
using StarWarsData.Services;
using Xunit.Abstractions;

namespace StarWarsData.Tests;

/// <summary>
/// Integration tests that validate the full Ask AI pipeline:
/// MCP (MongoDB) → Agent → OpenAI model → render_chart → AskChart deserialization.
///
/// Requires environment variables:
///   STARWARS_OPENAI_KEY         — OpenAI API key
///   MDB_MCP_CONNECTION_STRING   — MongoDB connection string (e.g. mongodb://192.168.1.102:27017)
/// </summary>
public class AskAiPipelineTests(ITestOutputHelper output)
{
    private const string Model = "gpt-5-mini";

    private static readonly HashSet<string> AllowedMcpTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "find",
        "aggregate",
        "count",
        "list_collections",
        "list_databases",
        "collection_schema",
    };

    private static McpClient CreateMcpClient()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("MDB_MCP_CONNECTION_STRING")
            ?? throw new InvalidOperationException("MDB_MCP_CONNECTION_STRING env var not set");

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = "MongoDB",
                Command = "npx",
                Arguments =
                [
                    "-y",
                    "@mongodb-js/mongodb-mcp-server",
                    "--connectionString",
                    connectionString,
                    "--readOnly",
                ],
            }
        );

        return McpClient
            .CreateAsync(
                transport,
                new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(2) }
            )
            .GetAwaiter()
            .GetResult();
    }

    private static OpenAIClient CreateOpenAiClient()
    {
        var apiKey =
            Environment.GetEnvironmentVariable("STARWARS_OPENAI_KEY")
            ?? throw new InvalidOperationException("STARWARS_OPENAI_KEY env var not set");

        return new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { 
                NetworkTimeout = TimeSpan.FromMinutes(5) }
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_ForcePowersByAlignment_ReturnsChart()
    {
        const string question =
            "How many Force powers are light side vs dark side vs universal? Show as a bar chart.";
        var chart = await RunPipeline(question);

        Assert.NotNull(chart);
        Assert.NotNull(chart.Title);
        output.WriteLine($"Chart type: {chart.AskChartType}");
        output.WriteLine($"Title: {chart.Title}");
        output.WriteLine($"Series count: {chart.Series?.Count}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_CharacterFamilyTree_ReturnsFamilyTreeChart()
    {
        const string question = "Show me Luke Skywalker's family tree.";
        var chart = await RunPipeline(question);

        Assert.NotNull(chart);
        Assert.Equal(AskChartType.FamilyTree, chart.AskChartType);
        Assert.NotNull(chart.FamilyTreeCharacterId);
        Assert.NotNull(chart.FamilyTreeCharacterName);
        output.WriteLine(
            $"Family tree for: {chart.FamilyTreeCharacterName} (PageId={chart.FamilyTreeCharacterId})"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_BattlesByEra_ReturnsChart()
    {
        const string question = "How many battles occurred per era? Show as a pie chart.";
        var chart = await RunPipeline(question);

        Assert.NotNull(chart);
        output.WriteLine($"Chart type: {chart.AskChartType}");
        output.WriteLine($"Title: {chart.Title}");
    }

    private async Task<AskChart> RunPipeline(string question)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        output.WriteLine($"Question: {question}");
        output.WriteLine("Initializing MCP client...");

        await using var mcpClient = CreateMcpClient();
        var openAiClient = CreateOpenAiClient();

        var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
        output.WriteLine(
            $"MCP tools available ({mcpTools.Count}): {string.Join(", ", mcpTools.Select(t => t.Name))}"
        );

        var chartToolkit = new ChartToolkit();
        var tools = new List<AITool> { chartToolkit.AsAIFunction() };
        tools.AddRange(
            mcpTools
                .Select(t => t.WithName(t.Name.Replace('-', '_')))
                .Where(t => AllowedMcpTools.Contains(t.Name))
                .Cast<AITool>()
        );
        output.WriteLine($"Total tools registered: {tools.Count}");

        IChatClient chatClient = openAiClient.GetChatClient(Model).AsIChatClient();
        output.WriteLine($"Using model: {Model}");

        ChatClientAgent agent = chatClient.AsAIAgent(
            instructions: """
            You are a precise Star Wars data assistant that builds charts and family trees.

            GOAL:
            Answer the user's question by building a chart or family tree. Never ask the user for clarification — always make reasonable assumptions and proceed.

            DATABASE: starwars-extracted-infoboxes
            Each collection is named after the infobox type (e.g. Character, Battle, War, ForcePower, Species).
            Documents have a "Data" array of { Label, Values[], Links[] } objects.

            CAPABILITIES:
            1. Call MongoDB tools to fetch and aggregate data.
            2. Call render_chart to produce the final output.

            STRATEGY:
            1. Decide: chart or family tree?
            2. Family tree: find the character in the Character collection (match on name or "Titles" field), get their PageId, call render_chart with chartType=FamilyTree, familyTreeCharacterId (integer PageId), and familyTreeCharacterName.
            3. Chart: pick the best collection and aggregation. If ambiguous, make the most reasonable assumption and proceed.
            4. Call render_chart with the result.

            CHART TYPE SELECTION:
            - Bar: counts/comparisons across categories
            - Line: trends over time
            - Pie/Donut: proportions of a whole
            - StackedBar: multiple series across categories
            - TimeSeries: data points with actual dates
            - FamilyTree: family, relatives, ancestry

            RULES:
            - Never ask the user questions. Make assumptions and proceed.
            - No commentary in final output.
            - Always call render_chart exactly once as your final action.
            """,
            tools: tools
        );

        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(question, session, cancellationToken: cts.Token);

        output.WriteLine($"Response.Text: {response.Text?[..Math.Min(500, response.Text?.Length ?? 0)]}");

        Assert.NotNull(chartToolkit.Result);
        output.WriteLine($"render_chart called with chartType: {chartToolkit.Result.AskChartType}");
        return chartToolkit.Result;
    }
}
