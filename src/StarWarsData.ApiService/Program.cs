using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.ServiceDefaults;
using StarWarsData.Services;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
BsonSerializer.RegisterIdGenerator(typeof(Guid), GuidGenerator.Instance);
ConventionRegistry.Register(
    "EnumAsString",
    new ConventionPack { new EnumRepresentationConvention(BsonType.String) },
    _ => true
);

builder.AddServiceDefaults();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddAGUI();

builder
    .Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true,
        reloadOnChange: true
    )
    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
    .AddEnvironmentVariables();

builder.AddMongoDBClient(connectionName: "mongodb");

#pragma warning disable CS8634 // McpClient registration is intentionally nullable
builder
    .Services.AddOptions()
    .Configure<SettingsOptions>(builder.Configuration.GetSection(SettingsOptions.Settings))
    .AddLogging()
    .AddHttpContextAccessor()
    .AddDataProtection()
    .Services.AddSingleton<OpenAiStatusService>()
    .AddSingleton<AskRateLimiter>()
    .AddSingleton<UserSettingsService>()
    .AddSingleton<ByokChatClient>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
        var userSettingsService = sp.GetRequiredService<UserSettingsService>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ByokChatClient");

        return new ByokChatClient(
            openAiClient.GetChatClient(settings.OpenAiModel).AsIChatClient(),
            httpContextAccessor,
            userSettingsService,
            apiKey => new OpenAIClient(apiKey).GetChatClient(settings.OpenAiModel).AsIChatClient(),
            logger
        );
    })
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddSingleton<TemplateHelper>()
    .AddScoped<InfoboxToEventsTransformer>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<MapService>()
    .AddScoped<GalaxyEventsService>()
    .AddScoped<TerritoryControlService>()
    .AddScoped<GalaxyMapReadService>()
    // CharacterTimelineService is needed for read endpoints (list/get/search)
    // The ChatClient is only used by GenerateTimelineAsync (called from Admin app)
    .AddSingleton<CharacterTimelineChatClient>(sp =>
    {
        var settingsOptions = sp.GetRequiredService<IOptions<SettingsOptions>>();
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var inner = new ChatClientBuilder(
            openAiClient.GetChatClient(settingsOptions.Value.CharacterTimelineModel).AsIChatClient()
        ).Build();
        return new CharacterTimelineChatClient(inner);
    })
    .AddScoped<CharacterTimelineService>()
    .AddSingleton<CharacterTimelineTracker>()
    .AddSingleton<OpenAIClient>(serviceProvider =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        return new OpenAIClient(
            new ApiKeyCredential(settingsOptions.Value.OpenAiKey),
            new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) }
        );
    })
    .AddSingleton<CollectionFilters>()
    .AddScoped<KnowledgeGraphQueryService>()
    .AddScoped<ChatSessionService>()
    .AddSingleton<GraphRAGToolkit>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var mongoClient = sp.GetRequiredService<IMongoClient>();
        var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        return new GraphRAGToolkit(mongoClient, settings.DatabaseName, embedder);
    })
    .AddSingleton<IChatClient>(sp =>
        new ChatClientBuilder(
            sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient()
        )
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build()
    )
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        sp.GetRequiredService<OpenAIClient>()
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator()
    )
    .AddKeyedSingleton<McpClient?>(
        "mongodb-mcp",
        (sp, _) =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            var mcpUrl = sp.GetRequiredService<IConfiguration>()["MCP_MONGODB_URL"];

            if (string.IsNullOrEmpty(mcpUrl))
            {
                logger.LogWarning("MCP_MONGODB_URL not configured — MCP client disabled.");
                return null;
            }

            try
            {
                var endpoint = new Uri(new Uri(mcpUrl.TrimEnd('/')), "/mcp");
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions { Endpoint = endpoint }
                );
                logger.LogInformation("Initializing MongoDB MCP client at {Endpoint}...", endpoint);
                var client = McpClient
                    .CreateAsync(
                        transport,
                        new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(2) }
                    )
                    .GetAwaiter()
                    .GetResult();
                logger.LogInformation("MongoDB MCP client initialized.");
                return client;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to initialize MongoDB MCP client — MCP tools disabled."
                );
                return null;
            }
        }
    )
    .AddSingleton<AIAgent>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var mcpClient = sp.GetKeyedService<McpClient>("mongodb-mcp");
        var componentToolkit = new ComponentToolkit();
        var mongoClient = sp.GetRequiredService<IMongoClient>();
        var dataExplorer = new DataExplorerToolkit(
            mongoClient,
            sp.GetRequiredService<IOptions<SettingsOptions>>()
        );

        // Wiki text search — registered directly as a tool so the model sees it
        // (UseAIContextProviders does not reliably surface tools via AGUI streaming)
        var pagesCollection = mongoClient
            .GetDatabase(settings.DatabaseName)
            .GetCollection<BsonDocument>(Collections.Pages);
        var wikiSearchProvider = new StarWarsWikiSearchProvider(
            pagesCollection,
            sp.GetRequiredService<ILoggerFactory>()
        );

        var graphRAG = sp.GetRequiredService<GraphRAGToolkit>();

        var tools = new List<AITool>();
        tools.AddRange(componentToolkit.AsAIFunctions());
        tools.AddRange(dataExplorer.AsAIFunctions());
        tools.AddRange(graphRAG.AsAIFunctions());
        tools.Add(
            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => wikiSearchProvider.SearchAsync(query, ct),
                "search_wiki",
                "Search Star Wars wiki pages for background context, lore, and article text. "
                    + "Use when the user asks about history, events, explanations, or lore that goes beyond structured infobox data."
            )
        );
        if (mcpClient is not null)
        {
            var allowedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "find",
                "aggregate",
                "count",
            };
            var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
            tools.AddRange(
                mcpTools
                    .Select(t => t.WithName(t.Name.Replace('-', '_')))
                    .Where(t => allowedMcpTools.Contains(t.Name))
                    .Cast<AITool>()
            );
        }

        var instructions = AgentPrompt.Instructions;

        // BYOK chat client — wraps the server OpenAI client and swaps to user's key when available
        var byokClient = sp.GetRequiredService<ByokChatClient>();

        var chatClient = new ChatClientBuilder(byokClient)
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();

        // Lightweight classifier client for topic guardrail (always uses server key)
        var classifierClient = new ChatClientBuilder(
            openAiClient.GetChatClient("gpt-4o-mini").AsIChatClient()
        )
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();

        var aiStatus = sp.GetRequiredService<OpenAiStatusService>();
        var guardrailLogger = sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger("StarWarsTopicGuardrail");

        return chatClient
            .AsAIAgent(instructions: instructions, tools: tools)
            .AsBuilder()
            .UseStarWarsTopicGuardrail(classifierClient, aiStatus, guardrailLogger)
            .Build();
    });

builder.Services.AddCors(options =>
{
    // API is internal-only (not exposed to the internet). Only the Blazor Server
    // frontend can reach it. See eng/adr/001-internal-api-auth.md for rationale.
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.UseHttpsRedirection();
app.MapControllers();
app.MapGet(
    "/api/ai/status",
    (OpenAiStatusService status) =>
    {
        var report = status.GetHealthReport();
        return Results.Ok(new { status = report.Status.ToString(), report.ErrorsLastHour });
    }
);

// Rate limiting + BYOK detection middleware for /kernel/stream
app.Use(
    async (context, next) =>
    {
        if (
            context.Request.Path.StartsWithSegments("/kernel/stream")
            && context.Request.Method == "POST"
        )
        {
            var rateLimiter = context.RequestServices.GetRequiredService<AskRateLimiter>();
            var userSettings = context.RequestServices.GetRequiredService<UserSettingsService>();
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
            var isAuthenticated = !string.IsNullOrEmpty(userId);
            var hasByok = false;

            if (isAuthenticated)
            {
                hasByok = await userSettings.HasOpenAiKeyAsync(userId!);
                context.Items["HasByok"] = hasByok;
            }

            // BYOK users skip rate limiting entirely
            if (!hasByok)
            {
                var clientIp =
                    context
                        .Request.Headers["X-Forwarded-For"]
                        .FirstOrDefault()
                        ?.Split(',')[0]
                        .Trim()
                    ?? context.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";
                var clientId = userId ?? $"anon:{clientIp}";
                var result = rateLimiter.TryAcquire(clientId, isAuthenticated);

                if (!result.Allowed)
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = (
                        (int)(result.RetryAfter?.TotalSeconds ?? 1800)
                    ).ToString();
                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            error = "Rate limit exceeded",
                            limit = result.Limit,
                            isAuthenticated,
                            retryAfterSeconds = (int)(result.RetryAfter?.TotalSeconds ?? 1800),
                        }
                    );
                    return;
                }
            }
        }

        await next();
    }
);

app.MapAGUI("/kernel/stream", app.Services.GetRequiredService<AIAgent>());
app.Run();
