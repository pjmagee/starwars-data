using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
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
using StarWarsData.Services.AI.Agents;
using StarWarsData.Services.AI.Agents.CharacterTimelines;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
BsonSerializer.RegisterIdGenerator(typeof(Guid), GuidGenerator.Instance);
ConventionRegistry.Register("EnumAsString", new ConventionPack { new EnumRepresentationConvention(BsonType.String) }, _ => true);

builder.AddServiceDefaults();
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddAGUI();
builder.Services.AddResponseCaching();

builder
    .Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
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
    .AddSingleton<SearchRateLimiter>()
    .AddSingleton<UserSettingsService>()
    .AddHttpClient<KeycloakAdminService>()
    .Services.AddSingleton<ByokChatClient>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
        var userSettingsService = sp.GetRequiredService<UserSettingsService>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ByokChatClient");

        return new ByokChatClient(
            openAiClient.GetResponsesClient().AsIChatClient(settings.OpenAiModel),
            httpContextAccessor,
            userSettingsService,
            apiKey => new OpenAIClient(apiKey).GetResponsesClient().AsIChatClient(settings.OpenAiModel),
            logger
        );
    })
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<TemplateHelper>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<MapService>()
    .AddScoped<GalaxyMapReadService>()
    // CharacterTimelineService is needed for read endpoints (list/get/search)
    // The ChatClient is only used by GenerateTimelineAsync (called from Admin app)
    .AddSingleton<CharacterTimelineChatClient>(sp =>
    {
        var settingsOptions = sp.GetRequiredService<IOptions<SettingsOptions>>();
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var inner = new ChatClientBuilder(openAiClient.GetResponsesClient().AsIChatClient(settingsOptions.Value.CharacterTimelineModel)).Build();
        return new CharacterTimelineChatClient(inner);
    })
    .AddScoped<CharacterTimelineService>()
    .AddSingleton<CharacterTimelineTracker>()
    .AddSingleton<OpenAIClient>(serviceProvider =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        return new OpenAIClient(new ApiKeyCredential(settingsOptions.Value.OpenAiKey), new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) });
    })
    .AddSingleton<CollectionFilters>()
    .AddSingleton<KnowledgeGraphQueryService>()
    .AddSingleton<SemanticSearchService>()
    .AddSingleton<KeywordSearchService>()
    .AddSingleton<OpenAiSpendQueryService>()
    .AddSingleton<StarWarsData.Services.Suggestions.SuggestionService>()
    .AddSingleton<HolocronAgent>()
    .AddSingleton<HolocronJobService>()
    .AddSingleton<StarWarsData.Services.AI.Agents.Holocron.HolocronEnhancementTracker>()
    .AddSingleton<StarWarsData.Services.AI.Agents.Holocron.HolocronAuditService>()
    .AddScoped<StarWarsData.Services.AI.Agents.Holocron.HolocronVerifierService>()
    .AddScoped<StarWarsData.Services.AI.Agents.Holocron.HolocronEnhancementService>()
    .AddScoped<ChatSessionService>()
    .AddSingleton<GraphRAGToolkit>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var mongoClient = sp.GetRequiredService<IMongoClient>();
        var kgService = sp.GetRequiredService<KnowledgeGraphQueryService>();
        var search = sp.GetRequiredService<SemanticSearchService>();
        return new GraphRAGToolkit(kgService, search, mongoClient, settings.DatabaseName);
    })
    .AddSingleton<IChatClient>(sp =>
        new ChatClientBuilder(sp.GetRequiredService<OpenAIClient>().GetResponsesClient().AsIChatClient("gpt-5.4-mini")).UseOpenTelemetry(configure: t => t.EnableSensitiveData = true).Build()
    )
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator())
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
                var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint });
                logger.LogInformation("Initializing MongoDB MCP client at {Endpoint}...", endpoint);
                var client = McpClient.CreateAsync(transport, new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(2) }).GetAwaiter().GetResult();
                logger.LogInformation("MongoDB MCP client initialized.");
                return client;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize MongoDB MCP client — MCP tools disabled.");
                return null;
            }
        }
    )
    .AddSingleton<AskAIAgent>()
    .AddSingleton<AIAgent>(sp => sp.GetRequiredService<AskAIAgent>().Build());

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

// Response caching must be wired before the endpoint executes so the controller's
// [ResponseCache(VaryByQueryKeys = ...)] attribute can work. Without it, the
// attribute throws InvalidOperationException at request time.
app.UseResponseCaching();

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
        if (context.Request.Path.StartsWithSegments("/kernel/stream") && context.Request.Method == "POST")
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

            // BYOK users and admins skip rate limiting entirely
            var isAdmin = context.Request.Headers["X-User-Roles"].FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries).Contains("admin", StringComparer.OrdinalIgnoreCase) ?? false;

            if (!hasByok && !isAdmin)
            {
                var clientIp = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var clientId = userId ?? $"anon:{clientIp}";
                var result = rateLimiter.TryAcquire(clientId, isAuthenticated);

                if (!result.Allowed)
                {
                    context.Response.StatusCode = 429;
                    context.Response.Headers["Retry-After"] = ((int)(result.RetryAfter?.TotalSeconds ?? 1800)).ToString();
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
