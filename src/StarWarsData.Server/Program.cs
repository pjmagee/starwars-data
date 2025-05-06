using Microsoft.AspNetCore.Components;
using MongoDB.Bson.Serialization;
using MudBlazor.Services;
using StarWarsData.Models;
using StarWarsData.Server.Components;
using StarWarsData.Services.Data;
using StarWarsData.Services.Helpers;
using StarWarsData.Services.Mongo;
using Microsoft.SemanticKernel;
using MongoDB.Driver;
using OpenAI;

#pragma warning disable SKEXP0010
#pragma warning disable SKEXP0001

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables(prefix: "ASPNETCORE_")
    .AddEnvironmentVariables(prefix: "SWDATA_");

var settings = builder.Configuration.GetSection("Settings").Get<Settings>()!;

builder.Services
    .AddSingleton<IMongoDatabase>(sp =>
    {
        IMongoClient mongoClient = new MongoClient(settings.MongoConnectionString);
        IMongoDatabase mongoDb = mongoClient.GetDatabase(settings.MongoDbName);
        return mongoDb;
    })
    .AddMudServices()
    .AddResponseCompression()
    .AddResponseCaching()
    .AddHttpsRedirection(options => options.HttpsPort = 5001)
    .AddCors(options =>
        {
            options.AddPolicy("CorsPolicy", b => b
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod()
            );
        }
    )
    .AddControllers();

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var nav = sp.GetRequiredService<NavigationManager>();
    var client = new HttpClient();
    client.BaseAddress = new Uri(nav.BaseUri);
    return client;
});

builder.Services
    .AddSingleton(settings)
    .AddSingleton<MongoDefinitions>()
    .AddSingleton<CollectionFilters>()
    .AddSingleton<YearComparer>()
    .AddSingleton<YearHelper>()
    .AddSingleton<TemplateHelper>() // Register TemplateHelper
    .AddScoped<RecordToEventsTransformer>()
    .AddScoped<RecordService>()
    .AddScoped<TimelineService>()
    .AddScoped<BattleService>()
    .AddScoped<WarService>()
    .AddScoped<PowerService>()
    .AddScoped<CharacterService>();

// https://www.mongodb.com/docs/atlas/atlas-vector-search/ai-integrations/semantic-kernel/
// https://www.mongodb.com/docs/atlas/atlas-vector-search/ai-integrations/semantic-kernel-csharp/#std-label-semantic-kernel-csharp
// https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/out-of-the-box-connectors/mongodb-connector?pivots=programming-language-csharp
// CS0618: Class 'Microsoft.SemanticKernel.Connectors.MongoDB.MongoDBMemoryStore' is obsolete:
// 'The IMemoryStore abstraction is being obsoleted, use Microsoft.Extensions.VectorData and MongoDBVectorStore

var openAiClient = new OpenAIClient(settings.OpenAiKey);

builder.Services
    //.AddSingleton<MongoToolkit>()
    .AddOpenAIChatCompletion(
        modelId: "gpt-4o-mini", 
        openAIClient: openAiClient)
    .AddOpenAITextEmbeddingGeneration(
        modelId: settings.EmbeddingModelId,
        openAIClient: openAiClient)
    .AddMongoDBVectorStore(settings.MongoConnectionString, settings.MongoDbName)
    .AddSingleton<KernelPluginCollection>(sp => [
        //KernelPluginFactory.CreateFromObject(sp.GetRequiredService<MongoToolkit>())
    ])
    .AddTransient(sp =>
    {
        KernelPluginCollection pluginCollection = sp.GetRequiredService<KernelPluginCollection>();
        return new Kernel(sp, pluginCollection);
    });

BsonClassMap.RegisterClassMap(new RecordClassMap());

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapControllers();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(StarWarsData.Client._Imports).Assembly);

app.Run();