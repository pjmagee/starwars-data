using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
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

    // Family-tree edge-case fixture PageIds (Design-042). Kept as const so the
    // FamilyTreeProjectionTests can reference them by name instead of magic numbers.
    public const int LukePageId = 1;
    public const int AnakinPageId = 2;
    public const int LeiaPageId = 3;
    public const int PadmePageId = 4;
    public const int ShmiPageId = 5;
    public const int HanSoloPageId = 6;
    public const int BenSoloPageId = 7;
    public const int BailOrganaPageId = 8;
    public const int OrganaFamilyPageId = 9;

    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static MongoDbContainer? _container;
    private static IMongoClient? _mongoClient;
    private static RecordService? _recordService;
    private static RelationshipAnalystToolkit? _toolkit;
    private static KnowledgeGraphQueryService? _kgQuery;

    public static IMongoClient MongoClient => _mongoClient ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static RecordService RecordService => _recordService ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static RelationshipAnalystToolkit RelationshipAnalystToolkit =>
        _toolkit ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

    public static KnowledgeGraphQueryService KnowledgeGraphQueryService =>
        _kgQuery ?? throw new InvalidOperationException("ApiFixture not initialized — call EnsureInitializedAsync from [ClassInitialize]");

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

            // Seed kg.nodes + kg.edges directly with the family-tree edge-case
            // matrix from specs/042-family-tree-component/data-model.md. We skip
            // Phase 5 (InfoboxGraphService) entirely — these tests assert the
            // BuildFamilyTreeAsync projection, not the ETL.
            var nodes = client.GetDatabase(DatabaseName).GetCollection<GraphNode>(Collections.KgNodes);
            await nodes.InsertManyAsync(BuildKgNodeSeed());

            var edges = client.GetDatabase(DatabaseName).GetCollection<RelationshipEdge>(Collections.KgEdges);
            await edges.InsertManyAsync(BuildKgEdgeSeed());

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
            _kgQuery = new KnowledgeGraphQueryService(client, settings);
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
                    Prop("Gender", ["Male"]),
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
                    Prop("Gender", ["Male"]),
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
                    Prop("Gender", ["Female"]),
                    Prop("Parent(s)", ["Anakin Skywalker"], [Link("Anakin Skywalker", "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends")]),
                    Prop("Sibling(s)", ["Luke Skywalker"], [Link("Luke Skywalker", "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends")]),
                ],
                "Leia Organa Solo was a Force-sensitive Human female."
            ),
            // ── Family-tree fixture additions (Design-042) ───────────────────────────
            // Padmé Amidala — Anakin's spouse, mother of Luke + Leia.
            MakePage(
                PadmePageId,
                "Padmé Amidala",
                "https://starwars.fandom.com/wiki/Padmé_Amidala",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Canon,
                [Prop("Titles", ["Padmé Amidala", "Padmé Naberrie"]), Prop("Born", ["46 BBY"]), Prop("Died", ["19 BBY"]), Prop("Gender", ["Female"])],
                "Padmé Amidala was a Naboo senator and the wife of Anakin Skywalker."
            ),
            // Shmi Skywalker — Anakin's mother.
            MakePage(
                ShmiPageId,
                "Shmi Skywalker",
                "https://starwars.fandom.com/wiki/Shmi_Skywalker",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Canon,
                [Prop("Titles", ["Shmi Skywalker"]), Prop("Born", ["72 BBY"]), Prop("Died", ["22 BBY"]), Prop("Gender", ["Female"])],
                "Shmi Skywalker was the mother of Anakin Skywalker."
            ),
            // Han Solo — Leia's spouse, father of Ben Solo. Deliberately MISSING
            // the Gender infobox field so the missing-gender soft-handle test fires
            // (data-model.md edge case #7).
            MakePage(
                HanSoloPageId,
                "Han Solo",
                "https://starwars.fandom.com/wiki/Han_Solo",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Canon,
                [Prop("Titles", ["Han Solo"]), Prop("Born", ["32 BBY"]), Prop("Died", ["34 ABY"])],
                "Han Solo was a smuggler turned Rebel Alliance general."
            ),
            // Ben Solo — son of Han and Leia. Note: existing seed has Ben Skywalker
            // as Luke's child (kept intact); Ben Solo is a separate entity here.
            MakePage(
                BenSoloPageId,
                "Ben Solo",
                "https://starwars.fandom.com/wiki/Ben_Solo",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Canon,
                [Prop("Titles", ["Ben Solo", "Kylo Ren"]), Prop("Born", ["5 ABY"]), Prop("Died", ["35 ABY"]), Prop("Gender", ["Male"])],
                "Ben Solo, also known as Kylo Ren, was the son of Han Solo and Leia Organa."
            ),
            // Bail Organa — Leia's adoptive father. The adoptive relationship is
            // expressed only via Organa family membership (no parent_of edge) so
            // the AdoptiveRelationsExcluded test fires.
            MakePage(
                BailOrganaPageId,
                "Bail Organa",
                "https://starwars.fandom.com/wiki/Bail_Organa",
                "https://starwars.fandom.com/wiki/Template:Character",
                Continuity.Canon,
                [Prop("Titles", ["Bail Prestor Organa"]), Prop("Born", ["67 BBY"]), Prop("Died", ["0 BBY"]), Prop("Gender", ["Male"])],
                "Bail Organa was a senator from Alderaan and adoptive father of Leia."
            ),
            // Organa family — a Family aggregate node. Used for the family-membership
            // edge that Leia gains via her adoption into the Organa household.
            MakePage(
                OrganaFamilyPageId,
                "Organa family",
                "https://starwars.fandom.com/wiki/Organa_family",
                "https://starwars.fandom.com/wiki/Template:Family",
                Continuity.Canon,
                [Prop("Titles", ["Organa family"])],
                "The Organa family was the royal House of Alderaan."
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

    // ── kg.* seed (Design-042) ───────────────────────────────────────────────
    // Direct kg.nodes / kg.edges seed for the family-tree edge-case matrix in
    // specs/042-family-tree-component/data-model.md § Edge cases. We skip the
    // Phase 5 builder entirely — these are projected docs verbatim. Each
    // Character carries `properties["Gender"]` matching its raw.pages.infobox
    // entry, because the generic NodeBuilder loop projects "Gender" via
    // FieldSemantics.Properties. BuildFamilyTreeAsync reads from kg.nodes,
    // not raw.pages (Principle VI). Han Solo deliberately omits Gender so
    // the missing-gender soft-handle path stays under test.
    public static List<GraphNode> BuildKgNodeSeed() =>
        [
            KgChar(LukePageId, "Luke Skywalker", Continuity.Legends, gender: "Male"),
            KgChar(AnakinPageId, "Anakin Skywalker", Continuity.Legends, gender: "Male"),
            KgChar(LeiaPageId, "Leia Organa Solo", Continuity.Legends, gender: "Female"),
            KgChar(PadmePageId, "Padmé Amidala", Continuity.Canon, gender: "Female"),
            KgChar(ShmiPageId, "Shmi Skywalker", Continuity.Canon, gender: "Female"),
            // Han Solo: no Gender property — triggers the missing-gender soft-
            // handle path in BuildFamilyTreeAsync (PageId appended to
            // Limitations.MissingGenders, Data.Gender defaults to "M").
            KgChar(HanSoloPageId, "Han Solo", Continuity.Canon, gender: null),
            KgChar(BenSoloPageId, "Ben Solo", Continuity.Canon, gender: "Male"),
            KgChar(BailOrganaPageId, "Bail Organa", Continuity.Canon, gender: "Male"),
            // Organa family aggregate — type=Family, NOT Character. Used as the
            // target of Leia's family-membership edge so the controller's
            // RootMustBeCharacter check has a non-Character node to reject.
            new GraphNode
            {
                PageId = OrganaFamilyPageId,
                Name = "Organa family",
                Type = KgNodeTypes.Family,
                Continuity = Continuity.Canon,
                Realm = Realm.Starwars,
                WikiUrl = "https://starwars.fandom.com/wiki/Organa_family",
            },
        ];

    private static GraphNode KgChar(int pageId, string name, Continuity continuity, string? gender) =>
        new()
        {
            PageId = pageId,
            Name = name,
            Type = KgNodeTypes.Character,
            Continuity = continuity,
            Realm = Realm.Starwars,
            WikiUrl = $"https://starwars.fandom.com/wiki/{name.Replace(' ', '_')}",
            Properties = gender is null ? new Dictionary<string, List<string>>() : new Dictionary<string, List<string>> { ["Gender"] = [gender] },
        };

    public static List<RelationshipEdge> BuildKgEdgeSeed() =>
        [
            // ── Skywalker line ───────────────────────────────────────────────
            // Anakin → parent_of → Luke (deliberately one-sided: no reverse
            // child_of edge, so the bidirectional-repair test fires).
            KgEdge(AnakinPageId, "Anakin Skywalker", LukePageId, "Luke Skywalker", "parent_of", Continuity.Legends),
            // Anakin → parent_of → Leia
            KgEdge(AnakinPageId, "Anakin Skywalker", LeiaPageId, "Leia Organa Solo", "parent_of", Continuity.Legends),
            // Padmé → parent_of → Luke + Leia
            KgEdge(PadmePageId, "Padmé Amidala", LukePageId, "Luke Skywalker", "parent_of", Continuity.Canon),
            KgEdge(PadmePageId, "Padmé Amidala", LeiaPageId, "Leia Organa Solo", "parent_of", Continuity.Canon),
            // Anakin ↔ Padmé partner_of (only one direction stored; repair pass
            // synthesizes the reverse).
            KgEdge(AnakinPageId, "Anakin Skywalker", PadmePageId, "Padmé Amidala", "partner_of", Continuity.Canon),
            // Shmi → parent_of → Anakin
            KgEdge(ShmiPageId, "Shmi Skywalker", AnakinPageId, "Anakin Skywalker", "parent_of", Continuity.Canon),
            // Luke ↔ Leia sibling_of (explicit, in addition to the implicit
            // shared-parents inference).
            KgEdge(LukePageId, "Luke Skywalker", LeiaPageId, "Leia Organa Solo", "sibling_of", Continuity.Legends),
            // ── Solo line ────────────────────────────────────────────────────
            // Han spouses Leia. Uses spouse_of (data-model says we union
            // partner_of ∪ spouse_of ∪ married_to into Rels.Spouses).
            KgEdge(HanSoloPageId, "Han Solo", LeiaPageId, "Leia Organa Solo", "spouse_of", Continuity.Canon),
            // Han + Leia → parent_of → Ben Solo
            KgEdge(HanSoloPageId, "Han Solo", BenSoloPageId, "Ben Solo", "parent_of", Continuity.Canon),
            KgEdge(LeiaPageId, "Leia Organa Solo", BenSoloPageId, "Ben Solo", "parent_of", Continuity.Canon),
            // ── Organa adoptive case ─────────────────────────────────────────
            // Leia ↔ Organa family membership. Bail is in the Organa family but
            // there's no parent_of Bail→Leia: the BFS sees Leia ↔ family ↔ Bail
            // and must emit an entry in Limitations.AdoptiveRelationsExcluded
            // WITHOUT adding Bail to Leia.Rels.Parents.
            KgEdge(LeiaPageId, "Leia Organa Solo", OrganaFamilyPageId, "Organa family", "family", Continuity.Canon),
            KgEdge(BailOrganaPageId, "Bail Organa", OrganaFamilyPageId, "Organa family", "family", Continuity.Canon),
            // ── has_relative edge (kinship-plugin test target) ───────────────
            // Single has_relative pair so the Kinship[] test has something to find.
            // Per data-model.md § Step 3 the edge surfaces only in Kinship — never
            // in Parents/Spouses/Children.
            KgEdge(LukePageId, "Luke Skywalker", BenSoloPageId, "Ben Solo", "has_relative", Continuity.Canon, qualifier: "Nephew"),
        ];

    private static RelationshipEdge KgEdge(int fromId, string fromName, int toId, string toName, string label, Continuity continuity, string? qualifier = null) =>
        new()
        {
            Id = ObjectId.GenerateNewId().ToString(),
            FromId = fromId,
            FromName = fromName,
            FromType = KgNodeTypes.Character,
            ToId = toId,
            ToName = toName,
            ToType = label == "family" ? KgNodeTypes.Family : KgNodeTypes.Character,
            Label = label,
            Continuity = continuity,
            Weight = 1.0,
            FromRealm = Realm.Starwars,
            ToRealm = Realm.Starwars,
            Meta = qualifier is null ? null : new EdgeMeta { Qualifier = qualifier },
        };
}
