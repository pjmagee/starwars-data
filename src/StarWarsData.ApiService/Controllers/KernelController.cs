using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using OpenAI;
using OpenAI.Chat;
using StarWarsData.Models;
using StarWarsData.Models.Queries;
using StarWarsData.Services;

namespace StarWarsData.ApiService.Controllers;

[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class KernelController : ControllerBase
{
    readonly ILogger<KernelController> _logger;
    readonly OpenAIClient _openAiClient;
    readonly SettingsOptions _settingsOptions;
    readonly McpClient _mcpClient;

    public KernelController(
        ILogger<KernelController> logger,
        OpenAIClient openAiClient,
        IOptions<SettingsOptions> settingsOptions,
        McpClient mcpClient
    )
    {
        _logger = logger;
        _openAiClient = openAiClient;
        _settingsOptions = settingsOptions.Value;
        _mcpClient = mcpClient;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<AskChart>> Ask(
        [FromBody] UserPrompt p,
        CancellationToken cancellationToken = default
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        _logger.LogInformation("Starting MCP client for question: {Question}", p.Question);

        var mcpTools = await _mcpClient.ListToolsAsync(cancellationToken: cts.Token);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "MCP tools available ({Count}): {Tools}",
                mcpTools.Count,
                string.Join(", ", mcpTools.Select(t => t.Name))
            );

        var allowedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "find",
            "aggregate",
            "count",
            "list_collections",
            "list_databases",
            "collection_schema",
        };

        var chartToolkit = new ChartToolkit();
        var tools = new List<AITool> { chartToolkit.AsAIFunction() };
        tools.AddRange(
            mcpTools
                .Select(t => t.WithName(t.Name.Replace('-', '_')))
                .Where(t => allowedMcpTools.Contains(t.Name))
                .Cast<AITool>()
        );
        _logger.LogInformation("Total tools registered: {Count}", tools.Count);

        ChatClient chatClient = _openAiClient.GetChatClient(_settingsOptions.OpenAiModel);
        _logger.LogInformation("Using model: {Model}", _settingsOptions.OpenAiModel);

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

        try
        {
            AgentSession session = await agent.CreateSessionAsync(cts.Token);
            AgentResponse response = await agent.RunAsync(
                p.Question,
                session,
                cancellationToken: cts.Token
            );

            _logger.LogInformation(
                "Response.Text: {Text}",
                response.Text?[..Math.Min(500, response.Text?.Length ?? 0)]
            );

            if (chartToolkit.Result is not null)
            {
                _logger.LogInformation("render_chart was called, returning typed result");
                return Ok(chartToolkit.Result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during agent run");
            return StatusCode(500, ex.Message);
        }

        return NoContent();
    }
}
