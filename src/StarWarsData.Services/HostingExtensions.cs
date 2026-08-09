using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;
using OpenAI;
using StarWarsData.Models;
using StarWarsData.Services.AI.Agents.CharacterTimelines;

namespace StarWarsData.Services;

/// <summary>
/// Shared host bootstrap for ApiService and Admin: BSON conventions, settings binding,
/// OpenAI client/embeddings, and the domain services both hosts consume.
/// Host-specific registrations (AGUI, Hangfire, rate limiters, MCP) stay in each Program.cs.
/// </summary>
public static class HostingExtensions
{
    public static TBuilder AddStarWarsDataCore<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        BsonSerializer.RegisterIdGenerator(typeof(Guid), GuidGenerator.Instance);
        ConventionRegistry.Register("EnumAsString", new ConventionPack { new EnumRepresentationConvention(BsonType.String) }, _ => true);

        builder
            .Services.Configure<SettingsOptions>(builder.Configuration.GetSection(SettingsOptions.Settings))
            .AddSingleton<OpenAiStatusService>()
            .AddSingleton<TemplateHelper>()
            .AddScoped<RecordService>()
            .AddScoped<TimelineService>()
            .AddSingleton<OpenAIClient>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
                return new OpenAIClient(new ApiKeyCredential(settings.OpenAiKey), new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) });
            })
            .AddSingleton<CharacterTimelineChatClient>(sp =>
            {
                var settings = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
                var openAiClient = sp.GetRequiredService<OpenAIClient>();
                var inner = new ChatClientBuilder(openAiClient.GetResponsesClient().AsIChatClient(settings.CharacterTimelineModel))
                    .UseOpenTelemetry(configure: t => t.EnableSensitiveData = true)
                    .Build();
                return new CharacterTimelineChatClient(inner);
            })
            .AddScoped<CharacterTimelineService>()
            .AddSingleton<CharacterTimelineTracker>()
            .AddSingleton<KnowledgeGraphQueryService>()
            .AddSingleton<SemanticSearchService>()
            .AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<OpenAIClient>().GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());

        return builder;
    }
}
