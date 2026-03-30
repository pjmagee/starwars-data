using System.ClientModel;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MudBlazor.Services;
using OpenAI;
using StarWarsData.Admin.Components;
using StarWarsData.Models;
using StarWarsData.ServiceDefaults;
using StarWarsData.Services;

var builder = WebApplication.CreateBuilder(args);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
BsonSerializer.RegisterIdGenerator(typeof(Guid), GuidGenerator.Instance);

builder.AddServiceDefaults();

builder.Services.AddControllers();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();

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
    .AddSingleton<OpenAiStatusService>()
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddSingleton<TemplateHelper>()
    .AddScoped<InfoboxToEventsTransformer>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<TerritoryControlService>()
    .AddScoped<TerritoryInferenceService>()
    .AddScoped<InfoboxGraphService>()
    .AddSingleton<OpenAIClient>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        return new OpenAIClient(
            new ApiKeyCredential(settings.OpenAiKey),
            new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) }
        );
    })
    .AddSingleton<RelationshipAnalystToolkit>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var mongoClient = sp.GetRequiredService<IMongoClient>();
        return new RelationshipAnalystToolkit(mongoClient, settings.DatabaseName);
    })
    .AddKeyedSingleton<IChatClient>(
        "relationship-analyst",
        (sp, _) =>
        {
            var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
            var openAiClient = sp.GetRequiredService<OpenAIClient>();
            return new ChatClientBuilder(
                openAiClient.GetChatClient(settings.RelationshipAnalystModel).AsIChatClient()
            )
                .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
                .Build();
        }
    )
    .AddSingleton<CharacterTimelineChatClient>(sp =>
    {
        var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
        var openAiClient = sp.GetRequiredService<OpenAIClient>();
        var inner = new ChatClientBuilder(
            openAiClient.GetChatClient(settings.CharacterTimelineModel).AsIChatClient()
        )
            .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
            .Build();
        return new CharacterTimelineChatClient(inner);
    })
    .AddScoped<CharacterTimelineService>()
    .AddSingleton<CharacterTimelineTracker>()
    .AddScoped<RelationshipGraphService>()
    .AddScoped<RelationshipGraphBuilderService>()
    .AddScoped<ArticleChunkingService>()
    .AddSingleton<PageDownloader>()
    .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        sp.GetRequiredService<OpenAIClient>()
            .GetEmbeddingClient("text-embedding-3-small")
            .AsIEmbeddingGenerator()
    );

builder.Services.AddHttpClient<PageDownloader>(
    (serviceProvider, client) =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        client.BaseAddress = new Uri(settingsOptions.Value.StarWarsBaseUrl);
        client.DefaultRequestHeaders.Add("User-Agent", "StarWarsData/1.0");
    }
);

var hangfireEnabled = builder.Configuration.GetSection("Settings").GetValue<bool>("HangfireEnabled", true);

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
                CheckQueuedJobsStrategy = CheckQueuedJobsStrategy.TailNotificationsCollection,
            }
        );
    }
);

if (hangfireEnabled)
{
    builder.Services.AddHangfireServer();
}

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseHangfireDashboard(
    "/hangfire",
    new DashboardOptions
    {
        Authorization = [new StarWarsData.Admin.AllowAllAuthorizationFilter()],
        IsReadOnlyFunc = _ => !hangfireEnabled,
    }
);

if (hangfireEnabled)
{
    // Daily incremental sync of changed wiki pages at 03:00 UTC
    RecurringJob.AddOrUpdate<PageDownloader>(
        "daily-incremental-sync",
        s => s.IncrementalSyncAsync(CancellationToken.None),
        Cron.Daily(3)
    );

    // Daily relationship graph builder at 04:00 UTC (after page sync completes)
    RecurringJob.AddOrUpdate<RelationshipGraphBuilderService>(
        "daily-relationship-graph-builder",
        s => s.ProcessAllAsync(100, CancellationToken.None),
        Cron.Daily(4)
    );

    // Submit one batch every 30 minutes
    RecurringJob.AddOrUpdate<RelationshipGraphBuilderService>(
        "submit-graph-batch",
        s => s.SubmitBatchAsync(CancellationToken.None),
        "*/30 * * * *"
    );

    // Check OpenAI batch status every 5 minutes
    RecurringJob.AddOrUpdate<RelationshipGraphBuilderService>(
        "check-graph-batches",
        s => s.CheckBatchesAsync(CancellationToken.None),
        "*/5 * * * *"
    );

    // Daily article chunking at 05:00 UTC (after graph builder completes)
    RecurringJob.AddOrUpdate<ArticleChunkingService>(
        "daily-article-chunking",
        s => s.ProcessAllAsync(CancellationToken.None),
        Cron.Daily(5)
    );
}

app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
