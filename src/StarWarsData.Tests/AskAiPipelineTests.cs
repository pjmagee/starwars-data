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
/// MCP (MongoDB) → Agent → OpenAI model → render_table / render_chart / render_graph.
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
            new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) }
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_ForcePowersByAlignment_ReturnsChart()
    {
        const string question =
            "How many Force powers are light side vs dark side vs universal? Show as a bar chart.";
        var toolkit = await RunPipeline(question);

        Assert.NotNull(toolkit.ChartResult);
        output.WriteLine($"Chart type: {toolkit.ChartResult.ChartType}");
        output.WriteLine($"Title: {toolkit.ChartResult.Title}");
        output.WriteLine($"Series count: {toolkit.ChartResult.Series?.Count}");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_CharacterRelationshipGraph_ReturnsGraph()
    {
        const string question = "Show me Luke Skywalker's relationship graph.";
        var toolkit = await RunPipeline(question);

        Assert.NotNull(toolkit.GraphResult);
        Assert.True(toolkit.GraphResult.RootEntityId > 0);
        Assert.NotEmpty(toolkit.GraphResult.RootEntityName);
        output.WriteLine(
            $"Relationship graph for: {toolkit.GraphResult.RootEntityName} (PageId={toolkit.GraphResult.RootEntityId})"
        );
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ask_BattlesByEra_ReturnsChart()
    {
        const string question = "How many battles occurred per era? Show as a pie chart.";
        var toolkit = await RunPipeline(question);

        Assert.NotNull(toolkit.ChartResult);
        output.WriteLine($"Chart type: {toolkit.ChartResult.ChartType}");
        output.WriteLine($"Title: {toolkit.ChartResult.Title}");
    }

    private async Task<ComponentToolkit> RunPipeline(string question)
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

        var componentToolkit = new ComponentToolkit();
        var tools = new List<AITool>();
        tools.AddRange(componentToolkit.AsAIFunctions());
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
            You are a precise Star Wars data assistant that builds charts, tables, and relationship graphs.

            GOAL:
            Answer the user's question by choosing the right visualization. Never ask for clarification.

            DATABASE: starwars-raw-pages
            Single "Pages" collection. Infobox type (Character, Battle, War, etc.) is in infobox.Template.
            Data exploration tools handle template filtering — just pass the type name.

            DOCUMENT SHAPE (Pages):
              _id          : integer PageId
              title        : string
              wikiUrl      : string — e.g. "https://starwars.fandom.com/wiki/Luke_Skywalker"
              continuity   : "Canon" | "Legends" | "Unknown"
              infobox.Data : array of { Label: string, Values: string[], Links: [{ Content, Href }] }

            COMMON Data Labels:
              Character : "Titles", "Born", "Died", "Parent(s)", "Partner(s)", "Sibling(s)", "Children", "Homeworld", "Species", "Affiliation"
              Battle    : "Date", "Outcome", "Conflict", "Place"
              War       : "Date", "Result", "Battles"
              ForcePower: "Alignment", "Area"
              Species   : "Average lifespan", "Homeworld", "Designation"
              Planet    : "Region", "Sector", "System"

            OUTPUT TOOLS (call exactly one as your final action):
            - render_table(title, collection, fields, search?, pageSize?) — configures a paginated table
            - render_chart(chartType, title, xAxisLabels?, labels?, series?, timeSeries?) — chart with aggregated data
            - render_graph(rootEntityId, rootEntityName, title, maxDepth?) — relationship graph

            STRATEGY:
            - For counts/comparisons: aggregate data, then render_chart
            - For listing entities: render_table with collection and relevant fields
            - For relationships/family trees: find entity by name, then render_graph with its _id

            RULES:
            - Never ask questions. Make assumptions and proceed.
            - Call exactly one output tool as your final action.
            """,
            tools: tools
        );

        AgentSession session = await agent.CreateSessionAsync(cts.Token);
        AgentResponse response = await agent.RunAsync(
            question,
            session,
            cancellationToken: cts.Token
        );

        output.WriteLine(
            $"Response.Text: {response.Text?[..Math.Min(500, response.Text?.Length ?? 0)]}"
        );

        var hasResult =
            componentToolkit.TableResult is not null
            || componentToolkit.ChartResult is not null
            || componentToolkit.GraphResult is not null;

        Assert.True(hasResult, "Expected at least one render tool to be called");

        if (componentToolkit.ChartResult is not null)
            output.WriteLine(
                $"render_chart called with chartType: {componentToolkit.ChartResult.ChartType}"
            );
        if (componentToolkit.GraphResult is not null)
            output.WriteLine(
                $"render_graph called for: {componentToolkit.GraphResult.RootEntityName}"
            );
        if (componentToolkit.TableResult is not null)
            output.WriteLine(
                $"render_table called for collection: {componentToolkit.TableResult.Collection}"
            );

        return componentToolkit;
    }
}
