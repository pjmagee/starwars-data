using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders;

/// <summary>
/// Single source of truth for the KG node-type → <see cref="INodeBuilder"/>
/// dispatch dictionary. Used by both <see cref="InfoboxGraphService"/> at
/// runtime and <c>NodeBuilderCoverageTests</c> so lockstep coverage cannot
/// drift from the real registration list.
/// </summary>
public static class NodeBuilderRegistry
{
    /// <summary>
    /// Build the full NodeType → builder dispatch dictionary.
    /// Adding a new node type means: add a constant to <see cref="KgNodeTypes"/>,
    /// create a real-logic builder under <c>NodeBuilders/Types/</c> (or register
    /// a <see cref="DefaultNodeBuilder"/> here), and add a single line below.
    /// </summary>
    public static Dictionary<string, INodeBuilder> CreateBuilders()
    {
        var builders = new Dictionary<string, INodeBuilder>(StringComparer.OrdinalIgnoreCase);
        void Register(INodeBuilder builder) => builders[builder.NodeType] = builder;

        // ── People ────────────────────────────────────────────────────────
        Register(new CharacterNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.Person));
        Register(new DefaultNodeBuilder(KgNodeTypes.Family));
        Register(new DefaultNodeBuilder(KgNodeTypes.Species));

        // ── Geography / Places ────────────────────────────────────────────
        Register(new CelestialBodyNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.Location));
        Register(new DefaultNodeBuilder(KgNodeTypes.City));
        Register(new DefaultNodeBuilder(KgNodeTypes.Structure));
        Register(new DefaultNodeBuilder(KgNodeTypes.System));
        Register(new SectorNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.Region));
        Register(new DefaultNodeBuilder(KgNodeTypes.Nebula));

        // ── Geography / Astronomy (additional) ────────────────────────────
        Register(new DefaultNodeBuilder(KgNodeTypes.Star));
        Register(new DefaultNodeBuilder(KgNodeTypes.Plant));

        // ── Politics / Military ───────────────────────────────────────────
        Register(new DefaultNodeBuilder(KgNodeTypes.Government));
        Register(new OrganizationNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.Company));
        // The type-name string is "Military_unit" (matches Wookieepedia's template
        // suffix); the C# constant is MilitaryUnit for PascalCase. The previous
        // MilitaryNodeBuilder used KgNodeTypes.Military ("Military") which never
        // matched any corpus row — replaced during the typed-NodeBuilders cleanup.
        Register(new DefaultNodeBuilder(KgNodeTypes.MilitaryUnit));
        Register(new DefaultNodeBuilder(KgNodeTypes.Fleet));

        // ── Events / Conflict ─────────────────────────────────────────────
        // Battle keeps a dedicated builder (Design-024 Pattern B entry point).
        // Other conflict types only need ConflictSideEncoder — parameterized here.
        Register(new BattleNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.War, (ctx, _, edges) => ConflictSideEncoder.StampSideIndex(ctx, edges)));
        Register(new DefaultNodeBuilder(KgNodeTypes.Campaign, (ctx, _, edges) => ConflictSideEncoder.StampSideIndex(ctx, edges)));
        Register(new DefaultNodeBuilder(KgNodeTypes.Mission, (ctx, _, edges) => ConflictSideEncoder.StampSideIndex(ctx, edges)));
        Register(new DefaultNodeBuilder(KgNodeTypes.Duel, (ctx, _, edges) => ConflictSideEncoder.StampSideIndex(ctx, edges)));
        Register(new DefaultNodeBuilder(KgNodeTypes.Election));
        Register(new DefaultNodeBuilder(KgNodeTypes.Event, (ctx, _, edges) => ConflictSideEncoder.StampSideIndex(ctx, edges)));
        Register(new DefaultNodeBuilder(KgNodeTypes.Treaty));
        Register(new DefaultNodeBuilder(KgNodeTypes.Era));
        // Design-024 Phase B: Chancellor (134), Chief (43), Head (25) are typed-leader
        // fields on Year pages — promoted from silent unclassified text to typed
        // relationships (has_chancellor, has_chief, has_head) via direct
        // FieldSemantics.Relationships entries. The generic loop picks them up
        // automatically — no OnFinalize override required.
        Register(new DefaultNodeBuilder(KgNodeTypes.Year));

        // ── Vehicles / Ships ──────────────────────────────────────────────
        Register(new DefaultNodeBuilder(KgNodeTypes.Starship));
        Register(new StarshipClassNodeBuilder());
        Register(new IndividualShipNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.SpaceStation));
        Register(new DefaultNodeBuilder(KgNodeTypes.Vehicle));
        Register(new DefaultNodeBuilder(KgNodeTypes.AirVehicle));
        Register(new DefaultNodeBuilder(KgNodeTypes.GroundVehicle));
        Register(new DefaultNodeBuilder(KgNodeTypes.RepulsorliftVehicle));
        Register(new TradeRouteNodeBuilder());

        // ── Things ────────────────────────────────────────────────────────
        Register(new DefaultNodeBuilder(KgNodeTypes.Weapon));
        Register(new DefaultNodeBuilder(KgNodeTypes.Lightsaber));
        Register(new DefaultNodeBuilder(KgNodeTypes.Device));
        Register(new DefaultNodeBuilder(KgNodeTypes.Artifact));
        Register(new DroidNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.DroidSeries));
        Register(new DefaultNodeBuilder(KgNodeTypes.Substance));
        Register(new DefaultNodeBuilder(KgNodeTypes.Food));
        Register(new DefaultNodeBuilder(KgNodeTypes.Clothing));
        Register(new DefaultNodeBuilder(KgNodeTypes.Armor));

        // ── Qualifier nodes ───────────────────────────────────────────────
        Register(new TitleOrPositionNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.ForcePower));
        Register(new DefaultNodeBuilder(KgNodeTypes.LightsaberForm));

        // ── Media — narrative ─────────────────────────────────────────────
        // Phase C — C3: book-shaped types normalise empty-label ISBN rows.
        Register(new DefaultNodeBuilder(KgNodeTypes.Book, IsbnNormalizer.NormalizeEmptyLabelToIsbn));
        Register(new DefaultNodeBuilder(KgNodeTypes.ShortStory));
        Register(new DefaultNodeBuilder(KgNodeTypes.Audiobook));
        Register(new DefaultNodeBuilder(KgNodeTypes.Movie));
        Register(new DefaultNodeBuilder(KgNodeTypes.Comic));
        Register(new DefaultNodeBuilder(KgNodeTypes.ComicStory));
        Register(new DefaultNodeBuilder(KgNodeTypes.ComicCollection));
        Register(new DefaultNodeBuilder(KgNodeTypes.Game));
        Register(new DefaultNodeBuilder(KgNodeTypes.Adventure));
        Register(new DefaultNodeBuilder(KgNodeTypes.ExpansionPack));

        // ── Media — periodicals + reference ───────────────────────────────
        Register(new DefaultNodeBuilder(KgNodeTypes.ReferenceBook, IsbnNormalizer.NormalizeEmptyLabelToIsbn));
        Register(new DefaultNodeBuilder(KgNodeTypes.ComicBook, IsbnNormalizer.NormalizeEmptyLabelToIsbn));
        Register(new DefaultNodeBuilder(KgNodeTypes.ComicMagazine));
        Register(new DefaultNodeBuilder(KgNodeTypes.MagazineIssue, IsbnNormalizer.NormalizeEmptyLabelToIsbn));
        Register(new DefaultNodeBuilder(KgNodeTypes.MagazineArticle));
        Register(new DefaultNodeBuilder(KgNodeTypes.ReferenceMagazine));
        Register(new TelevisionEpisodeNodeBuilder());
        Register(new DefaultNodeBuilder(KgNodeTypes.IuMedia));

        // Catch-all for unrecognised template types
        Register(new DefaultNodeBuilder(KgNodeTypes.Unknown));

        return builders;
    }
}
