using System.ClientModel;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
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

builder
    .Services.AddOptions()
    .Configure<SettingsOptions>(builder.Configuration.GetSection(SettingsOptions.Settings))
    .AddLogging()
    .AddHttpContextAccessor()
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddSingleton<TemplateHelper>()
    .AddScoped<InfoboxToEventsTransformer>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<MapService>()
    .AddSingleton<OpenAIClient>(serviceProvider =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        return new OpenAIClient(
            new ApiKeyCredential(settingsOptions.Value.OpenAiKey),
            new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) }
        );
    })
    .AddSingleton<CollectionFilters>()
    .AddScoped<RelationshipGraphService>()
    .AddSingleton<PageDownloader>()
    .AddSingleton<IChatClient>(sp =>
        new ChatClientBuilder(sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient())
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build()
    )
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        sp.GetRequiredService<OpenAIClient>()
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator()
    )
    .AddSingleton<McpClient>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
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
                    Environment.GetEnvironmentVariable("MDB_MCP_CONNECTION_STRING")!,
                    "--readOnly",
                ],
            }
        );
        logger.LogInformation("Initializing MongoDB MCP client...");
        var client = McpClient
            .CreateAsync(
                transport,
                new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(2) }
            )
            .GetAwaiter()
            .GetResult();
        logger.LogInformation("MongoDB MCP client initialized.");
        return client;
    })
    .AddSingleton<AIAgent>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var mcpClient = sp.GetRequiredService<McpClient>();

        var allowedMcpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "find",
            "aggregate",
            "count",
        };

        var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
        var componentToolkit = new ComponentToolkit();
        var dataExplorer = new DataExplorerToolkit(sp.GetRequiredService<IMongoClient>());
        var tools = new List<AITool>();
        tools.AddRange(componentToolkit.AsAIFunctions());
        tools.AddRange(dataExplorer.AsAIFunctions());
        tools.AddRange(
            mcpTools
                .Select(t => t.WithName(t.Name.Replace('-', '_')))
                .Where(t => allowedMcpTools.Contains(t.Name))
                .Cast<AITool>()
        );

        const string instructions = """
        You are a Star Wars data assistant. Answer questions with visualizations. Never ask for clarification.

        CONTINUITY: User messages start with [CONTINUITY: Canon|Legends|Both]. Add {"continuity":"Canon"} or {"continuity":"Legends"} to filters. "Both" = no filter.

        VISUALIZATION HINT: Messages may include [PREFER: chart|table|data_table|graph|timeline]. You MUST use the preferred output type unless it is truly impossible. This is the user's explicit choice.

        DATA MODEL: All data lives in database "starwars-raw-pages", collection "Pages". Each page may have an embedded infobox.
        The infobox type (Character, Battle, Species, Food, etc.) is stored in the infobox.Template field as a URL.
        All exploration tools accept an "infoboxType" parameter — this is NOT a MongoDB collection name. It is matched via regex against infobox.Template.
        Use list_infobox_types to discover valid infoboxType values.

        EXPLORATION TOOLS (prefer these over raw MongoDB):
        - search_pages_by_name(infoboxType, name) — find entities by name
        - get_page_by_id(infoboxType, id) — get full infobox for a known PageId
        - get_page_property(infoboxType, id, label) — get one property's values for a known PageId
        - search_pages_by_property(infoboxType, label, value) — find entities by a property value
        - search_pages_by_date(infoboxType, date) — find entities by BBY/ABY date
        - search_pages_by_link(infoboxType, wikiUrl) — find entities that reference a given wikiUrl
        - sample_property_values(infoboxType, label) — see distinct values for a property before aggregating
        - list_infobox_types() — discover valid infoboxType values
        - list_timeline_categories() — discover timeline category names
        - find, aggregate, count — raw MongoDB on starwars-raw-pages.Pages (use only when exploration tools are insufficient)

        OUTPUT (call exactly one as final action):
        - render_table(title, infoboxType, fields, search?, pageSize?) — paginated table, frontend fetches data. Do NOT query data yourself.
        - render_data_table(title, columns, rows) — ad-hoc table with data YOU provide from queries/aggregations.
        - render_chart(chartType, title, xAxisLabels?, labels?, series?, timeSeries?) — chart with data YOU aggregated.
        - render_graph(rootEntityId, rootEntityName, title, infoboxType?, maxDepth?, upLabels?, downLabels?, peerLabels?) — relationship graph, frontend fetches.
          CRITICAL: You MUST provide upLabels, downLabels, and peerLabels. Without them, no relationships are traversed.
          Before calling render_graph, use get_page_by_id to see the entity's infobox labels. Then pick ONLY the labels relevant to the user's question.
          - upLabels: labels where linked entities are above (e.g. Parent(s), Masters)
          - downLabels: labels where linked entities are below (e.g. Children, Apprentices)
          - peerLabels: labels where linked entities are same level (e.g. Partner(s), Sibling(s))
          IMPORTANT: Use maxDepth=1 for direct relationships (apprentices, children, masters). Only use maxDepth=2+ for full family trees.
          IMPORTANT: Include ONLY the ONE label category the user asked about. "apprentices" → downLabels=["Apprentices"] ONLY. Do NOT add Masters, Partners, Children, etc.
          Example: "Darth Vader's apprentices" → maxDepth=1, downLabels=["Apprentices"], upLabels=[], peerLabels=[]
          Example: "Skywalker family tree" → maxDepth=2, upLabels=["Parent(s)"], downLabels=["Children"], peerLabels=["Partner(s)","Sibling(s)"]
          DO NOT include every relationship label — only the ones the user asked about. Fewer labels = cleaner graph.
        - render_timeline(title, categories, pageSize?, yearFrom?, yearFromDemarcation?, yearTo?, yearToDemarcation?, search?) — timeline from starwars-timeline-events DB. Call list_timeline_categories first. Use year range + search to scope to a specific entity or period.

        RULES:
        - Call exactly one output tool as final action. No commentary after.
        - render_table/render_graph/render_timeline: frontend fetches data — don't query rows.
        - render_data_table/render_chart: YOU must query and provide the data.
        - render_table fields MUST match actual infobox.Data label names exactly (e.g. "Homeworld", "Species", "Born", "Died").
          Before calling render_table, use get_page_by_id on one result to see valid labels for that infobox type.
          Data is stored in both Links (hyperlinks) and Values (plain text) — the frontend reads both.
        - For "family tree" or "relationships" queries → use render_graph. Search by name to get PageId, then get_page_by_id to discover labels, then pick ONLY relevant labels for the query.
        - For entity-specific timelines (e.g. "timeline of Anakin Skywalker", "events during Anakin's life"):
          1. Look up the entity's date properties (Born, Died, Date, etc.) to get year range
          2. Pass yearFrom/yearFromDemarcation/yearTo/yearToDemarcation to render_timeline to scope events to that period
          3. Use the search parameter to filter event titles by the entity's name (e.g. "Skywalker")
        - Keep tool calls minimal — avoid unnecessary discovery calls.
        """;

        var chatClient = new ChatClientBuilder(openAiClient.GetChatClient(settings.OpenAiModel).AsIChatClient())
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();

        return chatClient.AsAIAgent(instructions: instructions, tools: tools);
    });

builder.Services.AddHangfire(
    (provider, config) =>
    {
        var settings = provider.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var mongoClient = provider.GetRequiredService<IMongoClient>();
        config.UseMongoStorage(
            mongoClient,
            settings.HangfireDb,
            new MongoStorageOptions
            {
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new DropMongoMigrationStrategy(),
                    BackupStrategy = new NoneMongoBackupStrategy(),
                },
                CheckConnection = true,
                /*
                 * Current db does not support change stream (not a replica set, https://docs.mongodb.com/manual/reference/method/db.collection.watch/)
                 * if you need instant (almost) handling of enqueued jobs, please set 'CheckQueuedJobsStrategy' to 'TailNotificationsCollection' in MongoStorageOptions
                 * MongoDB.Driver.MongoCommandException: Command aggregate failed: The $changeStream stage is only supported on replica sets.
                 */
                CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.TailNotificationsCollection,
            }
        );
    }
);

builder.Services.AddHangfireServer();

builder.Services.AddHttpClient<PageDownloader>(
    (serviceProvider, client) =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        client.BaseAddress = new Uri(settingsOptions.Value.StarWarsBaseUrl);
        client.DefaultRequestHeaders.Add("User-Agent", "StarWarsData/1.0");
    }
);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(); // Allow CORS for all origins

app.UseHangfireDashboard(
    "/hangfire",
    new DashboardOptions
    {
        Authorization = new[] { new StarWarsData.ApiService.AllowAllAuthorizationFilter() },
    }
);

// Daily incremental sync of changed wiki pages at 03:00 UTC
RecurringJob.AddOrUpdate<PageDownloader>(
    "daily-incremental-sync",
    s => s.IncrementalSyncAsync(CancellationToken.None),
    Cron.Daily(3));

app.UseHttpsRedirection();
app.MapControllers();
app.MapAGUI("/kernel/stream", app.Services.GetRequiredService<AIAgent>());
app.Run();
