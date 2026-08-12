using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using StarWarsData.Services;

namespace StarWarsData.Services.AI;

/// <summary>
/// Shared helpers to assemble the tool list for AskAIAgent / CopilotAgent / test fixtures
/// without duplicating the keyword_search wrapper, MCP filter logic, or classifier client setup.
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>
    /// Adds the wiki keyword_search tool using the canonical name and production description.
    /// </summary>
    public static void AddKeywordSearch(List<AITool> tools, StarWarsWikiSearchProvider wikiSearchProvider, JsonSerializerOptions? serializerOptions = null)
    {
        if (serializerOptions is null)
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string query, CancellationToken ct) => wikiSearchProvider.SearchAsync(query, ct),
                    ToolNames.Wiki.KeywordSearch,
                    """
                    Keyword search over wiki page titles and content. Fast, no AI cost.
                    Best for exact name lookups. For why/how/explain questions, use semantic_search instead.
                    """
                )
            );
        }
        else
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string query, CancellationToken ct) => wikiSearchProvider.SearchAsync(query, ct),
                    ToolNames.Wiki.KeywordSearch,
                    """
                    Keyword search over wiki page titles and content. Fast, no AI cost.
                    Best for exact name lookups. For why/how/explain questions, use semantic_search instead.
                    """,
                    serializerOptions: serializerOptions
                )
            );
        }
    }

    /// <summary>
    /// Adds the filtered read-only MCP tools (find/aggregate/count) if an McpClient is present.
    /// </summary>
    public static void AddMcpReadTools(List<AITool> tools, McpClient? mcpClient)
    {
        if (mcpClient is not null)
        {
            var allowedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "find", "aggregate", "count" };
            var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
            tools.AddRange(mcpTools.Select(t => t.WithName(t.Name.Replace('-', '_'))).Where(t => allowedMcpTools.Contains(t.Name)).Cast<AITool>());
        }
    }

    /// <summary>
    /// Creates the lightweight classifier chat client used for the StarWarsTopicGuardrail.
    /// Always uses the server key (gpt-5.4-mini) with OTel.
    /// </summary>
    public static IChatClient CreateClassifier(OpenAIClient openAiClient)
    {
        return new ChatClientBuilder(openAiClient.GetResponsesClient().AsIChatClient("gpt-5.4-mini"))
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();
    }
}
