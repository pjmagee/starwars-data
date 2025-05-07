using System.Text.Json;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using MongoDB.Driver;
using StarWarsData.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol.Transport;
using ModelContextProtocol.Protocol.Types;
using OpenAI.Chat;
using StarWarsData.Services.Plugins;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;

#pragma warning disable SKEXP0001

namespace StarWarsData.Server.Controllers;

[ApiController]
[Route("[controller]")]
[Produces("application/json")]
public class KernelController : ControllerBase
{
    readonly ILogger<KernelController> _logger;
    readonly Kernel _kernel;
    readonly IMongoDatabase _db;
    readonly Settings _settings;
    readonly IClientTransport _transport;

    public KernelController(ILogger<KernelController> logger, Kernel kernel, IMongoDatabase db, Settings settings)
    {
        _logger = logger;
        _kernel = kernel;
        _db = db;
        _settings = settings;
        _transport = new StdioClientTransport(new()
            {
                Name = "MongoDB",
                Command = "npx",
                Arguments = ["-y", "mongodb-mcp-server", "--connectionString", _settings.MongoConnectionString]
            }
        );
    }

    [HttpPost("ask")]
    public async Task<AskChart?> Ask([FromBody] UserPrompt p, CancellationToken cancellationToken = default)
    {
        await using IMcpClient mcpClient = await McpClientFactory.CreateAsync(_transport, cancellationToken: cancellationToken);
        var tools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        _kernel.Plugins.Clear();
        _kernel.Plugins.AddFromType<ChartToolkit>(pluginName: "ChartToolkit");
        IEnumerable<KernelFunction> functions = tools.Select(aiFunction => aiFunction.WithName(aiFunction.Name.Replace('-', '_')).AsKernelFunction());
        _kernel.Plugins.AddFromFunctions(pluginName: "MongoDB", functions: functions);

        OpenAIPromptExecutionSettings settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(options: new FunctionChoiceBehaviorOptions()
                {
                    AllowConcurrentInvocation = true,
                    AllowStrictSchemaAdherence = false,
                    RetainArgumentTypes = true,
                    AllowParallelCalls = true
                }
            ),
            Temperature = 0,
        };

        var chatHistory = new ChatHistory();

        // 4️⃣ Build your chat history and let SK do the function‐calling
        chatHistory.AddSystemMessage(
            """
            You are a helpful chart building assistant.

            IMPORTANT: 
            ONLY EXECUTE READ COMMANDS AND QUERIES. DO NOT EXECUTE WRITE/MODIFY COMMANDS.
            
            You are to assist in providing a chart based on the users question.

            1. Find the closest collection in the database relevant to the users question.
            2. Find relevant data from the records in the collection.
            3. End with calling the render_chart function from the ChartToolkit plugin.
            
            Your final message should contain ONLY the JSON output of the render_chart function.
            """
        );

        chatHistory.AddUserMessage(p.Question);

        var chatCompletion = _kernel.GetRequiredService<IChatCompletionService>();

        try
        {
            var chatMessage = await chatCompletion
                .GetChatMessageContentAsync(
                    chatHistory: chatHistory,
                    kernel: _kernel,
                    executionSettings: settings,
                    cancellationToken: cancellationToken
                );

            var chartJson = chatHistory
                .Where(chat => chat.Role == AuthorRole.Tool)
                .SelectMany(x => x.Items.OfType<FunctionResultContent>())
                .Where(x => x.FunctionName == "render_chart")
                .Select(x => x.Result?.ToString())
                .LastOrDefault();

            if (!string.IsNullOrWhiteSpace(chartJson))
            {
                return JsonSerializer.Deserialize<AskChart>(chartJson);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during chat completion");
        }

        return null;
    }
}