using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Services;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// Shared MongoDB Testcontainer + seed dataset used by integration tests.
/// Lazy-initialized on first access so unit-only test runs do not pay the
/// container startup cost.
/// </summary>
public static class ApiFixture
{
    public const string DatabaseName = "test-starwars";

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static MongoDbContainer? _container;
    private static IMongoClient? _mongoClient;
    private static RecordService? _recordService;
    private static RelationshipAnalystToolkit? _toolkit;

    public static IMongoClient MongoClient => _mongoClient ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static RecordService RecordService => _recordService ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static RelationshipAnalystToolkit RelationshipAnalystToolkit =>
        _toolkit ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static async Task EnsureInitializedAsync()
    {
        if (_container is not null)
            return;

        await Lock.WaitAsync();
        try
        {
            if (_container is not null)
                return;

            var container = new MongoDbBuilder("mongo:8").Build();
            await container.StartAsync();

            var client = new MongoClient(container.GetConnectionString());

            var pagesCollection = client.GetDatabase(DatabaseName).GetCollection<Page>(Collections.Pages);
            await pagesCollection.InsertManyAsync(BuildSeedData());

            var textIndex = Builders<Page>.IndexKeys.Text(p => p.Title).Text(p => p.Content);
            await pagesCollection.Indexes.CreateOneAsync(new CreateIndexModel<Page>(textIndex));

            var settings = Options.Create(
                new SettingsOptions
                {
                    DatabaseName = DatabaseName,
                    StarWarsBaseUrl = "https://starwars.fandom.com",
                    OpenAiKey = "test-key",
                }
            );

            _mongoClient = client;
            _recordService = new RecordService(NullLogger<RecordService>.Instance, settings, client);
            _toolkit = new RelationshipAnalystToolkit(client, DatabaseName);
            _container = container;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static async Task DisposeAsync()
    {
        if (_container is null)
            return;
        await _container.DisposeAsync();
        _container = null;
        _mongoClient = null;
        _recordService = null;
        _toolkit = null;
    }

    public static List<Page> BuildSeedData() =>
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
                    Prop("Homeworld", ["Tatooine"], [Link("Tatooine", "https://starwars.fandom.com/wiki/Tatooine")]),
                    Prop("Species", ["Human"], [Link("Human", "https://starwars.fandom.com/wiki/Human")]),
                    Prop("Parent(s)", ["Anakin Skywalker"], [Link("Anakin Skywalker", "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends")]),
                    Prop("Sibling(s)", ["Leia Organa Solo"], [Link("Leia Organa Solo", "https://starwars.fandom.com/wiki/Leia_Organa_Solo/Legends")]),
                    Prop("Children", ["Ben Skywalker"], [Link("Ben Skywalker", "https://starwars.fandom.com/wiki/Ben_Skywalker")]),
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
                    Prop("Homeworld", ["Tatooine"], [Link("Tatooine", "https://starwars.fandom.com/wiki/Tatooine")]),
                    Prop(
                        "Children",
                        ["Luke Skywalker", "Leia Organa Solo"],
                        [Link("Luke Skywalker", "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends"), Link("Leia Organa Solo", "https://starwars.fandom.com/wiki/Leia_Organa_Solo/Legends")]
                    ),
                ],
                "Anakin Skywalker was the Chosen One."
            ),
            MakePage(
                3,
                "Leia Organa Solo",
                "https://starwars.fandom.com/wiki/Leia_Organa_Solo%2FLegends",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Legends,
                [
                    Prop("Titles", ["Leia Organa Solo"]),
                    Prop("Born", ["19 BBY"]),
                    Prop("Parent(s)", ["Anakin Skywalker"], [Link("Anakin Skywalker", "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends")]),
                    Prop("Sibling(s)", ["Luke Skywalker"], [Link("Luke Skywalker", "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends")]),
                ],
                "Leia Organa Solo was a Force-sensitive Human female."
            ),
            MakePage(
                100,
                "Tatooine",
                "https://starwars.fandom.com/wiki/Tatooine",
                "https://starwars.fandom.com/wiki/Template:Planet",
                Continuity.Canon,
                [Prop("Titles", ["Tatooine"]), Prop("Region", ["Outer Rim Territories"]), Prop("Sector", ["Arkanis sector"]), Prop("System", ["Tatoo system"]), Prop("Suns", ["2"])],
                "Tatooine was a sparsely inhabited desert planet."
            ),
            MakePage(
                200,
                "Millennium Falcon",
                "https://starwars.fandom.com/wiki/Millennium_Falcon",
                "https://starwars.fandom.com/wiki/Template:Starship",
                Continuity.Canon,
                [Prop("Titles", ["Millennium Falcon"]), Prop("Class", ["Light freighter"]), Prop("Manufacturer", ["Corellian Engineering Corporation"])],
                "The Millennium Falcon was a modified YT-1300."
            ),
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
            MakePage(400, "Unknown Entity", "https://starwars.fandom.com/wiki/Unknown_Entity", "https://starwars.fandom.com/wiki/Template:Character", Continuity.Unknown, [], ""),
            MakePage(
                500,
                "Battle of Yavin",
                "https://starwars.fandom.com/wiki/Battle_of_Yavin",
                "https://starwars.fandom.com/wiki/Template:Battle",
                Continuity.Canon,
                [
                    Prop("Titles", ["Battle of Yavin"]),
                    Prop("Date", ["0 BBY"]),
                    Prop("Location", ["Yavin system"], [Link("Yavin", "https://starwars.fandom.com/wiki/Yavin")]),
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
                [Prop("Titles", ["Battle of Endor"]), Prop("Date", ["4 ABY"]), Prop("Location", ["Endor system"])],
                "The Battle of Endor was a major battle."
            ),
        ];

    private static Page MakePage(int id, string title, string wikiUrl, string template, Continuity continuity, List<InfoboxProperty> data, string content) =>
        new()
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

    private static InfoboxProperty Prop(string label, List<string> values, List<HyperLink>? links = null) =>
        new()
        {
            Label = label,
            Values = values,
            Links = links ?? [],
        };

    private static HyperLink Link(string content, string href) => new() { Content = content, Href = href };
}
