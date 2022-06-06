using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Polly;
using StarWarsData.Models;
using StarWarsData.Services;

CommandLineBuilder BuildCommandLine()
{
    var root = new RootCommand();
    root.AddCommand(new Command("download") { Handler = CommandHandler.Create<IHost, CancellationToken>(DownloadInfoboxes) });
    root.AddCommand(new Command("process") { Handler = CommandHandler.Create<IHost, CancellationToken>(ProcessRelationships) });
    root.AddCommand(new Command("populate") { Handler = CommandHandler.Create<IHost, CancellationToken>(PopulateDatabase) });
    return new CommandLineBuilder(root);
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
                    .AddSingleton<EventTransformer>()
                    .AddSingleton<CollectionFilters>()
                    .AddSingleton(new MongoClient(new MongoUrl(settings.MongoConnectionString)))
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