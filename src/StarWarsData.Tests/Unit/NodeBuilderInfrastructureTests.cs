using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Phase A of Design-024 ships infrastructure with zero behaviour change. These tests
/// pin down the two new pieces of infrastructure:
/// <list type="number">
///   <item><see cref="NodeBuilderContext.NodeTypeByPageId"/> is constructible and round-trips.</item>
///   <item>Every edge emitted by the generic <see cref="NodeBuilderBase.Build"/> loop
///         carries <c>Meta.SourceFieldLabel</c> matching the originating infobox label.</item>
/// </list>
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class NodeBuilderInfrastructureTests
{
    [TestMethod]
    public void NodeBuilderContext_CanBeConstructed_WithNodeTypeByPageId()
    {
        var nodeTypeByPageId = new Dictionary<int, string> { [42] = KgNodeTypes.Character, [43] = KgNodeTypes.TitleOrPosition };

        var ctx = new NodeBuilderContext(
            PageId: 1,
            Title: "Test",
            Type: KgNodeTypes.Character,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: "hash-1",
            WikiUrl: "/wiki/Test",
            ImageUrl: null,
            DataItems: new BsonArray(),
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Character),
            WikiUrlToPageId: new Dictionary<string, int>(),
            NodeTypeByPageId: nodeTypeByPageId
        );

        Assert.IsNotNull(ctx.NodeTypeByPageId);
        Assert.AreEqual(2, ctx.NodeTypeByPageId.Count);
        Assert.AreEqual(KgNodeTypes.Character, ctx.NodeTypeByPageId[42]);
        Assert.AreEqual(KgNodeTypes.TitleOrPosition, ctx.NodeTypeByPageId[43]);
    }

    [TestMethod]
    public void GenericBuild_StampsSourceFieldLabel_OnEmittedEdges()
    {
        // Arrange a Character page whose infobox has a "Masters" relationship field
        // with one linked target. The generic loop should classify it as a relationship
        // (FieldSemantics maps "Masters" → apprentice_of) and emit one edge.
        const string sourceField = "Masters";
        const int sourcePageId = 100;
        const int targetPageId = 200;
        const string targetTitle = "Yoda";
        const string targetUrl = "/wiki/Yoda";

        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, sourceField },
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

        var ctx = new NodeBuilderContext(
            PageId: sourcePageId,
            Title: "Luke Skywalker",
            Type: KgNodeTypes.Character,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Luke_Skywalker",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Character),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = KgNodeTypes.Character }
        );

        var builder = new CharacterNodeBuilder();

        // Act
        var result = builder.Build(ctx);

        // Assert
        Assert.AreEqual(1, result.Edges.Count, "Expected exactly one edge from the Masters field.");
        var edge = result.Edges[0];
        Assert.AreEqual(sourcePageId, edge.FromId);
        Assert.AreEqual(targetPageId, edge.ToId);
        Assert.AreEqual("apprentice_of", edge.Label);
        Assert.IsNotNull(edge.Meta, "Phase A: every emitted edge must carry Meta so SourceFieldLabel is stamped.");
        Assert.AreEqual(sourceField, edge.Meta!.SourceFieldLabel, "Meta.SourceFieldLabel must match the originating infobox field label.");
    }

    [TestMethod]
    public void GenericBuild_StampsSourceFieldLabel_OnFallbackEdges()
    {
        // The fallback emission path (no primary-link Values match) is exercised when a field
        // has links but no Values — a pattern seen in some infoboxes. The edge should still
        // carry SourceFieldLabel even though no primary-link extraction happened.
        const string sourceField = "Masters";
        const int sourcePageId = 100;
        const int targetPageId = 200;
        const string targetTitle = "Yoda";
        const string targetUrl = "/wiki/Yoda";

        var dataItems = new BsonArray
        {
            new BsonDocument
            {
                { InfoboxBsonFields.Label, sourceField },
                { InfoboxBsonFields.Values, new BsonArray() }, // empty values forces fallback
                {
                    InfoboxBsonFields.Links,
                    new BsonArray
                    {
                        new BsonDocument { { InfoboxBsonFields.Content, targetTitle }, { InfoboxBsonFields.Href, targetUrl } },
                    }
                },
            },
        };

        var ctx = new NodeBuilderContext(
            PageId: sourcePageId,
            Title: "Luke Skywalker",
            Type: KgNodeTypes.Character,
            Continuity: Continuity.Canon,
            Realm: Realm.Starwars,
            ContentHash: null,
            WikiUrl: "/wiki/Luke_Skywalker",
            ImageUrl: null,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(KgNodeTypes.Character),
            WikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            NodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = KgNodeTypes.Character }
        );

        var builder = new CharacterNodeBuilder();
        var result = builder.Build(ctx);

        Assert.AreEqual(1, result.Edges.Count);
        var edge = result.Edges[0];
        Assert.IsNotNull(edge.Meta, "Phase A: fallback-path edges must also carry Meta.SourceFieldLabel.");
        Assert.AreEqual(sourceField, edge.Meta!.SourceFieldLabel);
        StringAssert.Contains(edge.Evidence, "fallback");
    }
}
