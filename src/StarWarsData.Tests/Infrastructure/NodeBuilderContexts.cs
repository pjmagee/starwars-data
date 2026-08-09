using MongoDB.Bson;
using StarWarsData.Models.Entities;
using StarWarsData.Services.KnowledgeGraph.Definitions;
using StarWarsData.Services.KnowledgeGraph.NodeBuilders;

namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// Shared factories for unit tests that need a single-linked-field
/// <see cref="NodeBuilderContext"/>. Collapses the ~40-line private scaffold
/// previously duplicated across the NodeBuilder test suite.
/// </summary>
public static class NodeBuilderContexts
{
    /// <summary>
    /// Build a context whose infobox has one row: <paramref name="fieldLabel"/> linking to a single target.
    /// </summary>
    public static NodeBuilderContext SingleLinkedField(
        string sourceType,
        string fieldLabel,
        string targetTitle,
        string targetType,
        string sourceTitle = "Source Page",
        int targetPageId = 200,
        Realm realm = Realm.Starwars,
        int sourcePageId = 100,
        Continuity continuity = Continuity.Canon)
    {
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

        return FromDataItems(
            sourceType,
            dataItems,
            sourceTitle: sourceTitle,
            sourcePageId: sourcePageId,
            realm: realm,
            continuity: continuity,
            wikiUrlToPageId: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [targetUrl] = targetPageId, [targetTitle] = targetPageId },
            nodeTypeByPageId: new Dictionary<int, string> { [targetPageId] = targetType }
        );
    }

    /// <summary>
    /// Raw overload for tests that need a custom <see cref="BsonArray"/> of data items
    /// (multi-row, empty values, empty-label ISBN, etc.) while still defaulting the
    /// boilerplate <see cref="NodeBuilderContext"/> arguments.
    /// </summary>
    public static NodeBuilderContext FromDataItems(
        string sourceType,
        BsonArray dataItems,
        string sourceTitle = "Source Page",
        int sourcePageId = 100,
        Realm realm = Realm.Starwars,
        Continuity continuity = Continuity.Canon,
        string? contentHash = null,
        string? wikiUrl = null,
        string? imageUrl = null,
        IReadOnlyDictionary<string, int>? wikiUrlToPageId = null,
        IReadOnlyDictionary<int, string>? nodeTypeByPageId = null)
    {
        wikiUrl ??= $"/wiki/{sourceTitle.Replace(' ', '_')}";

        return new NodeBuilderContext(
            PageId: sourcePageId,
            Title: sourceTitle,
            Type: sourceType,
            Continuity: continuity,
            Realm: realm,
            ContentHash: contentHash,
            WikiUrl: wikiUrl,
            ImageUrl: imageUrl,
            DataItems: dataItems,
            Definition: InfoboxDefinitionRegistry.ForTemplate(sourceType),
            WikiUrlToPageId: wikiUrlToPageId ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            NodeTypeByPageId: nodeTypeByPageId ?? new Dictionary<int, string>()
        );
    }
}
