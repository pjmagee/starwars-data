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
    .AddScoped<CharacterRelationsService>()
    .AddSingleton<InfoboxExtractor>()
    .AddSingleton<InfoboxRelationshipProcessor>()
    .AddSingleton<PageDownloader>()
    .AddSingleton<IChatClient>(sp =>
        sp.GetRequiredService<OpenAIClient>().GetChatClient("gpt-4o-mini").AsIChatClient()
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
            "list_collections",
            "list_databases",
            "collection_schema",
        };

        var mcpTools = mcpClient.ListToolsAsync().GetAwaiter().GetResult();
        var chartToolkit = new ChartToolkit();
        var dataExplorer = new DataExplorerToolkit(sp.GetRequiredService<IMongoClient>());
        var tools = new List<AITool> { chartToolkit.AsAIFunction() };
        tools.AddRange(dataExplorer.AsAIFunctions());
        tools.AddRange(
            mcpTools
                .Select(t => t.WithName(t.Name.Replace('-', '_')))
                .Where(t => allowedMcpTools.Contains(t.Name))
                .Cast<AITool>()
        );

        const string instructions = """
        You are a precise Star Wars data assistant that builds charts and family trees.

        GOAL:
        Answer the user's question by building a chart or family tree. Never ask the user for clarification — always make reasonable assumptions and proceed.

        CONTINUITY:
        The user's message begins with a tag like [CONTINUITY: Canon], [CONTINUITY: Legends], or [CONTINUITY: Both].
        - [CONTINUITY: Canon]   → add { "Continuity": "Canon" } to every MongoDB filter. Never include Legends documents.
        - [CONTINUITY: Legends] → add { "Continuity": "Legends" } to every MongoDB filter. Never include Canon documents.
        - [CONTINUITY: Both]    → no Continuity filter — include all documents.
        find filter example (Canon):     { "Continuity": "Canon", "Data": { "$elemMatch": { "Label": "Titles", "Values": { "$regex": "Luke", "$options": "i" } } } }
        aggregate $match example (Canon): { "$match": { "Continuity": "Canon", "Data.Label": "Alignment" } }

        DATABASE: starwars-extracted-infoboxes
        Collections are named after infobox types: Character, Battle, War, ForcePower, Species, Planet, Vehicle, Weapon, etc.
        Use list_collections to discover available collections when unsure.

        DOCUMENT SHAPE:
        Every document has these top-level fields:
          _id          : integer PageId (use this as familyTreeCharacterId for FamilyTree charts)
          PageTitle    : string — human-readable name, good for display
          WikiUrl      : string — wiki path, e.g. "/wiki/Luke_Skywalker"
          Continuity   : "Canon" | "Legends" | "Unknown" — top-level filterable field
          Data         : array of { Label: string, Values: string[], Links: [{ Content: string, Href: string }] }

        COMMON Data Labels by collection:
          Character : "Titles" (name), "Born", "Died", "Parent(s)", "Partner(s)", "Sibling(s)", "Children", "Homeworld", "Species", "Affiliation"
          Battle    : "Date", "Outcome", "Conflict", "Place"
          War       : "Date", "Result", "Battles"
          ForcePower: "Alignment", "Area" (Alter/Sense/Control)
          Species   : "Average lifespan", "Homeworld", "Designation"
          Planet    : "Region", "Sector", "System"

        QUERYING:
        - All infobox documents use a "Titles" label in Data as the canonical name. Always use $elemMatch to search by name across any collection:
            { "Data": { "$elemMatch": { "Label": "Titles", "Values": { "$regex": "<name>", "$options": "i" } } } }
        - Data.Values is an array — $unwind "$Data.Values" after $unwind "$Data" before grouping.
        - Born/Died values look like "19 BBY" / "35 ABY".

        CAPABILITIES:
        1. find_by_title(collection, name, continuity?, limit?)            — look up any entity by name via Data.Titles.Values
        2. get_by_id(collection, id)                                       — get full infobox for a known id
        3. get_label_values(collection, id, label)                         — get values of one label for a known id
        4. find_by_label_value(collection, label, value, continuity?, limit?) — find entities where a label matches a value (e.g. Homeworld=Tatooine)
        5. find_by_date(collection, date, dateLabel?, continuity?, limit?) — find entities by BBY/ABY date (e.g. '19 BBY', '4 ABY')
        6. find_related(collection, wikiUrl, continuity?, limit?)          — find entities that reference a given WikiUrl in their links
        7. sample_label_values(collection, label, continuity?)             — see real value patterns for a Data.Label before aggregating
        8. MongoDB tools (find, aggregate, count, list_collections, collection_schema) — for custom queries
        9. render_chart                                                     — your single final output action

        STRATEGY:
        1. Decide: chart or family tree?
        2. Family tree — do exactly this, no more:
           a. Call find_by_title(collection="Character", name="<name>", continuity=<from tag>), limit 1.
           b. If no result, DO NOT call render_chart. Output plain text: "Character not found in the database."
           c. If found, take its id and name, then call render_chart(chartType="FamilyTree", familyTreeCharacterId=<id>, familyTreeCharacterName=<name>, title=...).
        3. Chart: use find_by_title or sample_label_values to explore data, then aggregate, then render_chart.
        4. For counts/comparisons prefer aggregate over multiple find calls.

        RULES:
        - Never ask the user questions. Make assumptions and proceed.
        - No commentary — render_chart is your only output, UNLESS a family tree character is not found (then output a plain text explanation).
        - Always call render_chart exactly once as your final action, unless the character was not found.
        - For family trees: exactly one find call then render_chart — never more.
        """;

        return openAiClient
            .GetChatClient(settings.OpenAiModel)
            .AsIChatClient()
            .AsAIAgent(instructions: instructions, tools: tools);
    });

// Add Hangfire services
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
                    MigrationStrategy = new MigrateMongoMigrationStrategy(),
                    BackupStrategy = new CollectionMongoBackupStrategy(),
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

app.UseHttpsRedirection();
app.MapControllers();
app.MapAGUI("/kernel/stream", app.Services.GetRequiredService<AIAgent>());
app.Run();
