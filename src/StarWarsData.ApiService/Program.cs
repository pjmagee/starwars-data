using System.ClientModel;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using OpenAI;
using StarWarsData.ApiService.Jobs;
using StarWarsData.Models;
using StarWarsData.ServiceDefaults;
using StarWarsData.Services;

#pragma warning disable CS0618
#pragma warning disable SKEXP0010
#pragma warning disable SKEXP0001

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
    .AddEnvironmentVariables();

builder.AddMongoDBClient(connectionName: "mongodb");

builder.Services
    .AddOptions()
    .Configure<SettingsOptions>(builder.Configuration.GetSection(SettingsOptions.Settings))
    .AddLogging()
    .AddHttpContextAccessor()
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddSingleton<TemplateHelper>()
    .AddScoped<RecordToEventsTransformer>()
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
        return new OpenAIClient(new ApiKeyCredential(settingsOptions.Value.OpenAiKey));
    })
    .AddSingleton<CollectionFilters>()
    .AddScoped<CharacterRelationsService>()
    .AddSingleton<InfoboxDownloader>()
    .AddSingleton<InfoboxRelationshipProcessor>()
    .AddSingleton<PageDownloader>()
    .AddSingleton<IBackgroundJobQueue, BackgroundJobQueue>()
    .AddHostedService<BackgroundJobHostedService>()
    .AddOpenAIChatCompletion(modelId: "gpt-4o-mini")
    .AddOpenAITextEmbeddingGeneration(modelId: "text-embedding-3-small")
    .AddSingleton<KernelPluginCollection>(sp => [])
    .AddTransient(sp =>
    {
        KernelPluginCollection pluginCollection = sp.GetRequiredService<KernelPluginCollection>();
        return new Kernel(sp, pluginCollection);
    });

builder.Services
    .AddHttpClient<InfoboxDownloader>((serviceProvider, client) =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        client.BaseAddress = new Uri(settingsOptions.Value.StarWarsBaseUrl);
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(10));

builder.Services
    .AddHttpClient<PageDownloader>((serviceProvider, client) =>
    {
        var settingsOptions = serviceProvider.GetRequiredService<IOptions<SettingsOptions>>();
        client.BaseAddress = new Uri(settingsOptions.Value.StarWarsBaseUrl);
        client.DefaultRequestHeaders.Add("User-Agent", "StarWarsData/1.0");
    });

BsonClassMap.RegisterClassMap(new RecordClassMap());

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();  // Allow CORS for all origins

app.UseHttpsRedirection();
app.MapControllers();
app.Run();