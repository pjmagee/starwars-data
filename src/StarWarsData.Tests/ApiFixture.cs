using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests;

/// <summary>
/// Shared MongoDB container fixture for service-level integration tests.
/// Seeds a Pages collection with diverse infobox types for testing
/// RecordService and RelationshipAnalystToolkit.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private MongoDbContainer _container = null!;

    public IMongoClient MongoClient { get; private set; } = null!;
    public RecordService RecordService { get; private set; } = null!;
    public RelationshipAnalystToolkit RelationshipAnalystToolkit { get; private set; } = null!;

    public const string DatabaseName = "test-starwars";

    public async Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8").Build();

        await _container.StartAsync();

        MongoClient = new MongoClient(_container.GetConnectionString());

        var pagesCollection = MongoClient
            .GetDatabase(DatabaseName)
            .GetCollection<Page>(Collections.Pages);
        await pagesCollection.InsertManyAsync(BuildSeedData());

        // Create text index on title + content — required by RecordService.GetSearchResult
        var textIndex = Builders<Page>.IndexKeys.Text(p => p.Title).Text(p => p.Content);
        await pagesCollection.Indexes.CreateOneAsync(new CreateIndexModel<Page>(textIndex));

        var settings = Options.Create(
            new SettingsOptions
            {
                DatabaseName = DatabaseName,
                HangfireDb = "test-hangfire",
                StarWarsBaseUrl = "https://starwars.fandom.com",
                OpenAiKey = "test-key",
            }
        );

        var yearHelper = new YearHelper(new YearComparer());
        var templateHelper = new TemplateHelper();
        var transformer = new InfoboxToEventsTransformer(
            NullLogger<InfoboxToEventsTransformer>.Instance,
            templateHelper,
            yearHelper
        );

        // Stub embedding generator — not needed for query tests
        var embeddingGen = new NoOpEmbeddingGenerator();

        RecordService = new RecordService(
            NullLogger<RecordService>.Instance,
            settings,
            yearHelper,
            MongoClient,
            embeddingGen,
            transformer
        );

        RelationshipAnalystToolkit = new RelationshipAnalystToolkit(MongoClient, DatabaseName);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// Builds a diverse seed dataset covering multiple infobox types,
    /// null/empty edge cases, and relationship links.
    /// </summary>
    public static List<Page> BuildSeedData()
    {
        return
        [
            // Characters
            MakePage(
                1,
                "Luke Skywalker",
                "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Legends,
                [
                    Prop("Titles", ["Luke Skywalker"]),
                    Prop("Born", ["19 BBY"]),
                    Prop("Died", [""]),
                    Prop(
                        "Homeworld",
                        ["Tatooine"],
                        [Link("Tatooine", "https://starwars.fandom.com/wiki/Tatooine")]
                    ),
                    Prop(
                        "Species",
                        ["Human"],
                        [Link("Human", "https://starwars.fandom.com/wiki/Human")]
                    ),
                    Prop(
                        "Parent(s)",
                        ["Anakin Skywalker"],
                        [
                            Link(
                                "Anakin Skywalker",
                                "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends"
                            ),
                        ]
                    ),
                    Prop(
                        "Sibling(s)",
                        ["Leia Organa Solo"],
                        [
                            Link(
                                "Leia Organa Solo",
                                "https://starwars.fandom.com/wiki/Leia_Organa_Solo/Legends"
                            ),
                        ]
                    ),
                    Prop(
                        "Children",
                        ["Ben Skywalker"],
                        [Link("Ben Skywalker", "https://starwars.fandom.com/wiki/Ben_Skywalker")]
                    ),
                ],
                "Luke Skywalker was a Force-sensitive Human male Jedi Master."
            ),
            MakePage(
                2,
                "Anakin Skywalker",
                "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Legends,
                [
                    Prop("Titles", ["Anakin Skywalker"]),
                    Prop("Born", ["41 BBY"]),
                    Prop("Died", ["4 ABY"]),
                    Prop(
                        "Homeworld",
                        ["Tatooine"],
                        [Link("Tatooine", "https://starwars.fandom.com/wiki/Tatooine")]
                    ),
                    Prop(
                        "Children",
                        ["Luke Skywalker", "Leia Organa Solo"],
                        [
                            Link(
                                "Luke Skywalker",
                                "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends"
                            ),
                            Link(
                                "Leia Organa Solo",
                                "https://starwars.fandom.com/wiki/Leia_Organa_Solo/Legends"
                            ),
                        ]
                    ),
                ],
                "Anakin Skywalker was the Chosen One."
            ),
            MakePage(
                3,
                "Leia Organa Solo",
                "https://starwars.fandom.com/wiki/Leia_Organa_Solo%2FLegends", // encoded URL
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Legends,
                [
                    Prop("Titles", ["Leia Organa Solo"]),
                    Prop("Born", ["19 BBY"]),
                    Prop(
                        "Parent(s)",
                        ["Anakin Skywalker"],
                        [
                            Link(
                                "Anakin Skywalker",
                                "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends"
                            ),
                        ]
                    ),
                    Prop(
                        "Sibling(s)",
                        ["Luke Skywalker"],
                        [
                            Link(
                                "Luke Skywalker",
                                "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends"
                            ),
                        ]
                    ),
                ],
                "Leia Organa Solo was a Force-sensitive Human female."
            ),
            // Planet
            MakePage(
                100,
                "Tatooine",
                "https://starwars.fandom.com/wiki/Tatooine",
                "https://starwars.fandom.com/wiki/Template:Planet",
                Continuity.Canon,
                [
                    Prop("Titles", ["Tatooine"]),
                    Prop("Region", ["Outer Rim Territories"]),
                    Prop("Sector", ["Arkanis sector"]),
                    Prop("System", ["Tatoo system"]),
                    Prop("Suns", ["2"]),
                ],
                "Tatooine was a sparsely inhabited desert planet."
            ),
            // Starship
            MakePage(
                200,
                "Millennium Falcon",
                "https://starwars.fandom.com/wiki/Millennium_Falcon",
                "https://starwars.fandom.com/wiki/Template:Starship",
                Continuity.Canon,
                [
                    Prop("Titles", ["Millennium Falcon"]),
                    Prop("Class", ["Light freighter"]),
                    Prop("Manufacturer", ["Corellian Engineering Corporation"]),
                ],
                "The Millennium Falcon was a modified YT-1300."
            ),
            // Page with null infobox (should be filtered from categories)
            new Page
            {
                PageId = 300,
                Title = "Disambiguation Page",
                WikiUrl = "https://starwars.fandom.com/wiki/Disambiguation",
                Continuity = Continuity.Unknown,
                Content = "This is a disambiguation page.",
                Categories = [],
                Images = [],
                Infobox = null,
            },
            // Page with empty Data array in infobox
            MakePage(
                400,
                "Unknown Entity",
                "https://starwars.fandom.com/wiki/Unknown_Entity",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Unknown,
                [],
                ""
            ),
            // Battle (for timeline-related tests)
            MakePage(
                500,
                "Battle of Yavin",
                "https://starwars.fandom.com/wiki/Battle_of_Yavin",
                "https://starwars.fandom.com/wiki/Template:Battle",
                Continuity.Canon,
                [
                    Prop("Titles", ["Battle of Yavin"]),
                    Prop("Date", ["0 BBY"]),
                    Prop(
                        "Location",
                        ["Yavin system"],
                        [Link("Yavin", "https://starwars.fandom.com/wiki/Yavin")]
                    ),
                    Prop("Outcome", ["Rebel Alliance victory"]),
                ],
                "The Battle of Yavin was a decisive battle."
            ),
            MakePage(
                501,
                "Battle of Endor",
                "https://starwars.fandom.com/wiki/Battle_of_Endor",
                "https://starwars.fandom.com/wiki/Template:Battle",
                Continuity.Canon,
                [
                    Prop("Titles", ["Battle of Endor"]),
                    Prop("Date", ["4 ABY"]),
                    Prop("Location", ["Endor system"]),
                ],
                "The Battle of Endor was a major battle."
            ),
        ];
    }

    static Page MakePage(
        int id,
        string title,
        string wikiUrl,
        string template,
        Continuity continuity,
        List<InfoboxProperty> data,
        string content
    )
    {
        return new Page
        {
            PageId = id,
            Title = title,
            WikiUrl = wikiUrl,
            Continuity = continuity,
            Content = content,
            Categories = [],
            Images = [],
            Infobox = new PageInfobox { Template = template, Data = data },
        };
    }

    static InfoboxProperty Prop(string label, List<string> values, List<HyperLink>? links = null) =>
        new()
        {
            Label = label,
            Values = values,
            Links = links ?? [],
        };

    static HyperLink Link(string content, string href) => new() { Content = content, Href = href };
}

/// <summary>
/// No-op embedding generator for tests that don't need embeddings.
/// </summary>
internal sealed class NoOpEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } = new("no-op");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var embeddings = values.Select(_ => new Embedding<float>(new float[] { 0f })).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }
