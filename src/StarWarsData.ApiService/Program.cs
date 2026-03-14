using System.ClientModel;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.AI;
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
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
BsonSerializer.RegisterIdGenerator(typeof(Guid), GuidGenerator.Instance);

builder.AddServiceDefaults();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

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
    .Services
    .AddOptions()
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
    .AddScoped<BattleService>()
    .AddScoped<WarService>()
    .AddScoped<PowerService>()
    .AddScoped<CharacterService>()
    .AddScoped<MapService>()
    .AddScoped<FamilyService>()
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
        sp.GetRequiredService<OpenAIClient>()
            .GetChatClient("gpt-4o-mini")
            .AsIChatClient()
    )
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        sp.GetRequiredService<OpenAIClient>()
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator()
    )
    .AddSingleton<McpClient>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<Program>>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
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
        });
        logger.LogInformation("Initializing MongoDB MCP client...");
        var client = McpClient.CreateAsync(transport, new McpClientOptions
        {
            InitializationTimeout = TimeSpan.FromMinutes(2)
        }).GetAwaiter().GetResult();
        logger.LogInformation("MongoDB MCP client initialized.");
        return client;
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
app.Run();
