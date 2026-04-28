using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Design-024 Phase C — C2 for <see cref="TelevisionEpisodeNodeBuilder"/>:
/// Guest star promotion + Production company promotion via FieldSemantics, plus
/// the <c>Timeline (Canon)</c> / <c>Timeline (Legends)</c> continuity-suffix
/// relabel done in <see cref="TelevisionEpisodeNodeBuilder.OnFinalize"/>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class TelevisionEpisodeNodeBuilderTests
{
    static NodeBuilderContext BuildCtx(string fieldLabel, string targetTitle, string targetType)
    {
        const int targetPageId = 200;
        var targetUrl = $"/wiki/{targetTitle.Replace(' ', '_')}";

        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, fieldLabel },
                {
                    InfoboxBsonFields.Values,
                    new BsonArray { targetTitle }
                },
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, targetTitle }, { InfoboxBsonFields.Href, targetUrl } },
                    }
                },
            },
        };

        return new NodeBuilderContext(
            PageId: 100,
            Title: "Test Episode",
            Type: KgNodeTypes.TelevisionEpisode,
            Continuity: Continuity.Canon,
            Realm: Realm.Real,
            ContentHash: null,
            WikiUrl: "/wiki/Test_Episode",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.TelevisionEpisode),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );
    }

    [TestMethod]
    public void GuestStar_PromotesToFeaturedActor()
    {
        var ctx = BuildCtx("Guest star(s)", "Some Actor", KgNodeTypes.Person);
        var result = new TelevisionEpisodeNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("featured_actor", result.Edges[0].Label, "Guest star(s) should map to canonical featured_actor.");
    }

    [TestMethod]
    public void ProductionCompany_PromotesToProducedBy()
    {
        var ctx = BuildCtx("Production company", "Lucasfilm", "Company");
        var result = new TelevisionEpisodeNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("produced_by", result.Edges[0].Label);
    }

    [TestMethod]
    [DataRow("Timeline  (Canon)")] // Note: TWO spaces (matches TemplateFields.g.cs)
    [DataRow("Timeline  (Legends)")]
    [DataRow("Timeline (Canon)")] // tolerate single-space variant too
    [DataRow("Timeline (Legends)")]
    public void TimelineWithContinuitySuffix_CollapsesToInTimeline(string fieldLabel)
    {
        // Targets "21 BBY" — note: target type Year would normally be dropped by
        // the post-processing filter, but that's at the InfoboxGraphService coordinator
        // level, NOT the per-builder level. The builder unit test only cares that the
        // label is correctly normalised. Use a non-Year target to keep the edge alive
        // through the unit-test assertion.
        var ctx = BuildCtx(fieldLabel, "Some Era", KgNodeTypes.Era);
        var result = new TelevisionEpisodeNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("in_timeline", result.Edges[0].Label, $"Field '{fieldLabel}' should collapse to in_timeline.");
        Assert.AreEqual(fieldLabel, result.Edges[0].Meta?.SourceFieldLabel);
    }

    [TestMethod]
    public void PlainTimeline_NotAffectedByOverride()
    {
        // The plain "Timeline" field is already canonical via FieldSemantics; the
        // override regex must NOT match it (no parenthetical).
        var ctx = BuildCtx("Timeline", "21 BBY", KgNodeTypes.Era);
        var result = new TelevisionEpisodeNodeBuilder().Build(ctx);
        Assert.AreEqual(1, result.Edges.Count);
        Assert.AreEqual("in_timeline", result.Edges[0].Label);
    }
}
