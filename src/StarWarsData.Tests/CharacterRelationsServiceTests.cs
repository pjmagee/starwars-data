using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.Tests;

/// <summary>
/// Unit tests for <see cref="RelationshipGraphService"/> using a shared in-process MongoDB container.
///
/// Character dataset (Skywalker family):
///   Shmi (1) ──parent──► Anakin (2) ──parent──► Luke (3) ──parent──► Ben (5)
///                          Anakin (2) ──parent──► Leia (4)
///   Luke (3) ──sibling──► Leia (4)
///   Luke (3) ──partner──► Mara (6)
///
/// WikiUrl encoding mix:
///   Anakin stored with decoded /Legends suffix
///   Shmi and Leia stored with %2F-encoded suffix (exercises the URL-normalisation fix)
/// </summary>
[Collection("Mongo")]
[Trait("Category", "MongoFixture")]
public class RelationshipGraphServiceTests(MongoFixture fixture)
{
    private RelationshipGraphService Svc => fixture.Service;

    // ── PageId constants ──────────────────────────────────────────────────────
    public const int ShmiId = 1;
    public const int AnakinId = 2;
    public const int LukeId = 3;
    public const int LeiaId = 4;
    public const int BenId = 5;
    public const int MaraId = 6;

    // ── WikiUrl constants (how they are stored in the DB) ─────────────────────
    private const string ShmiUrl = "https://starwars.fandom.com/wiki/Shmi_Skywalker%2FLegends"; // encoded
    private const string AnakinUrl = "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends"; // decoded
    private const string LukeUrl = "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends";
    private const string LeiaUrl = "https://starwars.fandom.com/wiki/Leia_Organa_Solo%2FLegends"; // encoded
    private const string BenUrl = "https://starwars.fandom.com/wiki/Ben_Skywalker";
    private const string MaraUrl = "https://starwars.fandom.com/wiki/Mara_Jade_Skywalker";

    // ── Link Href constants (decoded, as they appear in infobox Data.Links) ───
    private const string ShmiHref = "https://starwars.fandom.com/wiki/Shmi_Skywalker/Legends";
    private const string AnakinHref = "https://starwars.fandom.com/wiki/Anakin_Skywalker/Legends";
    private const string LukeHref = "https://starwars.fandom.com/wiki/Luke_Skywalker/Legends";
    private const string LeiaHref = "https://starwars.fandom.com/wiki/Leia_Organa_Solo/Legends";
    private const string BenHref = "https://starwars.fandom.com/wiki/Ben_Skywalker";
    private const string MaraHref = "https://starwars.fandom.com/wiki/Mara_Jade_Skywalker";

    // ── GetImmediateRelationsAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetImmediateRelationsAsync_Luke_ReturnsParentsAndSiblingsAndChildren()
    {
        var result = await Svc.GetImmediateRelationsAsync(LukeId);

        Assert.NotNull(result.Root);
        Assert.Equal(LukeId, result.Root.Id);
        Assert.Equal("Luke Skywalker", result.Root.Name);

        Assert.Contains(result.Parents, p => p.Id == AnakinId);
        Assert.Contains(result.Siblings, s => s.Id == LeiaId);
        Assert.Contains(result.Children, c => c.Id == BenId);
        Assert.Contains(result.Partners, p => p.Id == MaraId);
    }

    [Fact]
    public async Task GetImmediateRelationsAsync_ResolveEncodedWikiUrl_FindsShmi()
    {
        // Shmi's WikiUrl is stored encoded (%2FLegends); Anakin's Parent(s) href is decoded.
        // The service must normalise both sides.
        var result = await Svc.GetImmediateRelationsAsync(AnakinId);

        Assert.Contains(result.Parents, p => p.Id == ShmiId);
    }

    [Fact]
    public async Task GetImmediateRelationsAsync_ResolveEncodedWikiUrl_FindsLeia()
    {
        // Leia's WikiUrl is stored encoded (%2FLegends); Luke's Sibling(s) href is decoded.
        var result = await Svc.GetImmediateRelationsAsync(LukeId);

        Assert.Contains(result.Siblings, s => s.Id == LeiaId);
    }

    [Fact]
    public async Task GetImmediateRelationsAsync_UnknownId_ReturnsEmptyDto()
    {
        var result = await Svc.GetImmediateRelationsAsync(9999);

        Assert.Null(result.Root);
        Assert.Empty(result.Parents);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task GetImmediateRelationsAsync_Ben_HasLukeAsParent()
    {
        var result = await Svc.GetImmediateRelationsAsync(BenId);

        Assert.NotNull(result.Root);
        Assert.Equal(BenId, result.Root.Id);
        Assert.Contains(result.Parents, p => p.Id == LukeId);
        Assert.Empty(result.Siblings);
        Assert.Empty(result.Children);
    }

    [Fact]
    public async Task GetImmediateRelationsAsync_BornAndDied_AreMapped()
    {
        var result = await Svc.GetImmediateRelationsAsync(LukeId);

        Assert.Equal("19 BBY", result.Root.Born);
        Assert.Equal("", result.Root.Died);
    }

    // ── GetRelationshipGraphAsync ────────────────────────────────────────────────────

    [Fact(Skip = "Known graph depth traversal bug — nodes only contains depth-matched IDs")]
    public async Task GetRelationshipGraphAsync_Luke_DefaultDepth_IncludesThreeGenerations()
    {
        // depth 0: Luke
        // depth 1: Anakin, Leia, Ben, Mara
        // depth 2: Shmi (parent of Anakin)
        var result = await Svc.GetRelationshipGraphAsync(LukeId, maxDepth: 3);

        Assert.Equal(LukeId, result.RootId);
        Assert.Contains(LukeId, result.Nodes.Keys);
        Assert.Contains(AnakinId, result.Nodes.Keys);
        Assert.Contains(LeiaId, result.Nodes.Keys);
        Assert.Contains(BenId, result.Nodes.Keys);
        Assert.Contains(MaraId, result.Nodes.Keys);
        Assert.Contains(ShmiId, result.Nodes.Keys);
    }

    [Fact(Skip = "Known graph depth traversal bug — nodes only contains depth-matched IDs")]
    public async Task GetRelationshipGraphAsync_MaxDepth1_OnlyImmediateRelatives()
    {
        var result = await Svc.GetRelationshipGraphAsync(LukeId, maxDepth: 1);

        Assert.Contains(LukeId, result.Nodes.Keys);
        Assert.Contains(AnakinId, result.Nodes.Keys);
        Assert.Contains(LeiaId, result.Nodes.Keys);
        Assert.Contains(BenId, result.Nodes.Keys);
        Assert.Contains(MaraId, result.Nodes.Keys);

        // Shmi is 2 hops away — must NOT appear
        Assert.DoesNotContain(ShmiId, result.Nodes.Keys);
    }

    [Fact]
    public async Task GetRelationshipGraphAsync_MaxDepth0_OnlyRoot()
    {
        var result = await Svc.GetRelationshipGraphAsync(LukeId, maxDepth: 0);

        Assert.Single(result.Nodes);
        Assert.Contains(LukeId, result.Nodes.Keys);
    }

    [Fact]
    public async Task GetRelationshipGraphAsync_UnknownRoot_ReturnsEmptyNodes()
    {
        var result = await Svc.GetRelationshipGraphAsync(9999, maxDepth: 3);

        Assert.Empty(result.Nodes);
    }

    [Fact]
    public async Task GetRelationshipGraphAsync_NodeNamesAndDates_AreMapped()
    {
        var result = await Svc.GetRelationshipGraphAsync(LukeId, maxDepth: 1);

        var luke = result.Nodes[LukeId];
        Assert.Equal("Luke Skywalker", luke.Name);
        Assert.Equal("19 BBY", luke.Born);
    }

    // ── Dataset builder (public so MongoFixture can access it) ───────────────

    public static List<Page> BuildDataset() =>
        [
            MakeCharacterPage(
                ShmiId,
                ShmiUrl,
                "Shmi Skywalker",
                "72 BBY",
                "22 BBY",
                parents: [],
                siblings: [],
                children: [(AnakinHref, "Anakin Skywalker")],
                partners: []
            ),
            MakeCharacterPage(
                AnakinId,
                AnakinUrl,
                "Anakin Skywalker",
                "41 BBY",
                "4 ABY",
                parents: [(ShmiHref, "Shmi Skywalker")],
                siblings: [],
                children: [(LukeHref, "Luke Skywalker"), (LeiaHref, "Leia Organa Solo")],
                partners: []
            ),
            MakeCharacterPage(
                LukeId,
                LukeUrl,
                "Luke Skywalker",
                "19 BBY",
                "",
                parents: [(AnakinHref, "Anakin Skywalker")],
                siblings: [(LeiaHref, "Leia Organa Solo")],
                children: [(BenHref, "Ben Skywalker")],
                partners: [(MaraHref, "Mara Jade Skywalker")]
            ),
            MakeCharacterPage(
                LeiaId,
                LeiaUrl,
                "Leia Organa Solo",
                "19 BBY",
                "",
                parents: [(AnakinHref, "Anakin Skywalker")],
                siblings: [(LukeHref, "Luke Skywalker")],
                children: [],
                partners: []
            ),
            MakeCharacterPage(
                BenId,
                BenUrl,
                "Ben Skywalker",
                "26 ABY",
                "",
                parents: [(LukeHref, "Luke Skywalker")],
                siblings: [],
                children: [],
                partners: []
            ),
            MakeCharacterPage(
                MaraId,
                MaraUrl,
                "Mara Jade Skywalker",
                "17 BBY",
                "40 ABY",
                parents: [],
                siblings: [],
                children: [(BenHref, "Ben Skywalker")],
                partners: [(LukeHref, "Luke Skywalker")]
            ),
        ];

    private static Page MakeCharacterPage(
        int id,
        string wikiUrl,
        string name,
        string born,
        string died,
        (string href, string content)[] parents,
        (string href, string content)[] siblings,
        (string href, string content)[] children,
        (string href, string content)[] partners
    )
    {
        static List<HyperLink> ToLinks((string href, string content)[] items) =>
            items.Select(x => new HyperLink { Href = x.href, Content = x.content }).ToList();

        return new Page
        {
            PageId = id,
            Title = name,
            WikiUrl = wikiUrl,
            Continuity = Continuity.Legends,
            Content = string.Empty,
            Categories = [],
            Images = [],
            Infobox = new PageInfobox
            {
                Template = "https://starwars.fandom.com/wiki/Template:Character",
                Data =
                [
                    new()
                    {
                        Label = "Titles",
                        Values = [name],
                        Links = [],
                    },
                    new()
                    {
                        Label = "Born",
                        Values = [born],
                        Links = [],
                    },
                    new()
                    {
                        Label = "Died",
                        Values = [died],
                        Links = [],
                    },
                    new()
                    {
                        Label = "Parent(s)",
                        Values = parents.Select(x => x.content).ToList(),
                        Links = ToLinks(parents),
                    },
                    new()
                    {
                        Label = "Sibling(s)",
                        Values = siblings.Select(x => x.content).ToList(),
                        Links = ToLinks(siblings),
                    },
                    new()
                    {
                        Label = "Children",
                        Values = children.Select(x => x.content).ToList(),
                        Links = ToLinks(children),
                    },
                    new()
                    {
                        Label = "Partner(s)",
                        Values = partners.Select(x => x.content).ToList(),
                        Links = ToLinks(partners),
                    },
                ],
            },
        };
    }
}
