using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.MongoDB;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Polly;
using StarWarsData.Models;
using StarWarsData.Services.Mongo;
using StarWarsData.Services.Wiki;
using StarWarsData.Services.Helpers;
using StarWarsData.Services.Data;
using StarWarsData.Services.Plugins;

#pragma warning disable SKEXP0010

async Task DeleteVectorIndexes(IHost host, CancellationToken cancellationToken)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.DeleteVectorIndexesAsync(cancellationToken);
}

async Task CreateVectorIndexes(IHost arg1, CancellationToken arg2)
{
    using var scope = arg1.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.CreateVectorIndexesAsync(arg2);
}

async Task DeleteCollections(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.DeleteCollections(token);
}

async Task CreateTimeline(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.CreateTimelineEvents(token);
}

async Task CreateOpenAiEmbeddings(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.ProcessEmbeddingsAsync(token);
}

async Task DownloadInfoboxes(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<InfoboxDownloader>();
    await service.ExecuteAsync(token);
}

async Task PopulateDatabase(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<RecordService>();
    await service.PopulateAsync(token);
}

async Task ProcessRelationships(IHost host, CancellationToken token)
{
    using var scope = host.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<InfoboxRelationshipProcessor>();
    await service.ExecuteAsync(token);
}

CommandLineBuilder BuildCommandLine()
{
    var root = new RootCommand();
    
    // infobox download
    // infobox relationships
    var infoBoxCommand = new Command("infobox");
    infoBoxCommand.AddCommand(new Command("download") { Handler = CommandHandler.Create<IHost, CancellationToken>(DownloadInfoboxes) });
    infoBoxCommand.AddCommand(new Command("relationships") { Handler = CommandHandler.Create<IHost, CancellationToken>(ProcessRelationships) });
    root.AddCommand(infoBoxCommand);
    
    // mongo populate
    // mongo embedding
    var mongoCommand = new Command("mongo");
    mongoCommand.AddCommand(new Command("create-embeddings") { Handler = CommandHandler.Create<IHost, CancellationToken>(CreateOpenAiEmbeddings) });
    mongoCommand.AddCommand(new Command("delete-embeddings") { Handler = CommandHandler.Create<IHost, CancellationToken>(DeleteOpenAiEmbeddings) });

    async Task DeleteOpenAiEmbeddings(IHost host, CancellationToken token)
    {
        using var scope = host.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<RecordService>();
        await service.DeleteOpenAiEmbeddingsAsync(token);
    }

    mongoCommand.AddCommand(new Command("populate-database") { Handler = CommandHandler.Create<IHost, CancellationToken>(PopulateDatabase) });
    mongoCommand.AddCommand(new Command("delete-collections") { Handler = CommandHandler.Create<IHost, CancellationToken>(DeleteCollections) });
    mongoCommand.AddCommand(new Command("create-timeline") { Handler = CommandHandler.Create<IHost, CancellationToken>(CreateTimeline) });
    mongoCommand.AddCommand(new Command("create-index-embeddings") { Handler = CommandHandler.Create<IHost, CancellationToken>(CreateVectorIndexes) });
    mongoCommand.AddCommand(new Command("delete-index-embeddings") { Handler = CommandHandler.Create<IHost, CancellationToken>(DeleteVectorIndexes) });

    root.AddCommand(mongoCommand);
    
    return new CommandLineBuilder(root);
}

await BuildCommandLine()
    .UseHost(_ => Host.CreateDefaultBuilder(), hostBuilder =>
    {
        hostBuilder
            .ConfigureHostConfiguration(builder =>
            {
                builder
                    .AddJsonFile("hostsettings.json", optional: false)
                    .AddEnvironmentVariables(prefix: "DOTNET_");
            })
            .ConfigureAppConfiguration((context, builder) =>
            {
                builder
                    .AddJsonFile("appsettings.json", optional: false)
                    .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: false)
                    .AddEnvironmentVariables(prefix: "SWDATA_");
            })
            .ConfigureLogging((context, builder) =>
            {
                builder.AddConfiguration(context.Configuration.GetRequiredSection("Logging"));
                builder.AddConsole();
            })
            .ConfigureServices((context, services) =>
            {
                var settings = context.Configuration.GetSection(nameof(Settings)).Get<Settings>()!;

                services
                    .AddSingleton(settings)
                    .AddSingleton<InfoboxDownloader>()
                    .AddSingleton<InfoboxRelationshipProcessor>()
                    .AddSingleton<RecordService>()
                    .AddSingleton<YearComparer>() 
                    .AddSingleton<YearHelper>()
                    .AddSingleton<TemplateHelper>()
                    .AddSingleton<RecordToEventsTransformer>()
                    .AddSingleton<IMongoDatabase>(provider =>
                    {
                        MongoClient client = new MongoClient(settings.MongoConnectionString);
                        IMongoDatabase database = client.GetDatabase(settings.MongoDbName);
                        return database;
                    })
                    .AddMongoDBVectorStore()
                    .AddOpenAITextEmbeddingGeneration(modelId: settings.EmbeddingModelId, apiKey: settings.OpenAiKey)
                    .AddHttpClient();
                

                services
                    .AddHttpClient<InfoboxDownloader>()
                    .ConfigureHttpClient(client => client.BaseAddress = new Uri(settings.StarWarsBaseUrl))
                    .AddTransientHttpErrorPolicy(policyBuilder => policyBuilder.RetryAsync())
                    .SetHandlerLifetime(TimeSpan.FromMinutes(10));

                BsonClassMap.RegisterClassMap(new RecordClassMap());
            });
    })
    .UseDefaults()
    .Build()
    .InvokeAsync(args);