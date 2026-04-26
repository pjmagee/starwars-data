using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.TradeRoute"/> extraction. In addition to the generic
/// edge loop, stores ordered <c>{label}Ids</c> pageId sequences for every
/// relationship field so consumers can reconstruct the geographic route
/// without name-based joins.
/// </summary>
public sealed class TradeRouteNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.TradeRoute;

    protected override void OnRelationshipExtracted(NodeBuilderContext ctx, string label, IReadOnlyList<PrimaryLink> primaryLinks, Dictionary<string, List<string>> properties)
    {
        if (primaryLinks.Count == 0)
            return;

        var orderedIds = primaryLinks
            .OrderBy(pl => pl.Order)
            .Select(pl =>
            {
                var href = pl.Link.Contains(InfoboxBsonFields.Href) ? pl.Link[InfoboxBsonFields.Href].AsString : null;
                var content = pl.Link.Contains(InfoboxBsonFields.Content) ? pl.Link[InfoboxBsonFields.Content].AsString : null;
                return href is not null && content is not null ? ResolveLinkTarget(href, content, ctx.WikiUrlToPageId) : 0;
            })
            .Where(id => id > 0)
            .Select(id => id.ToString())
            .ToList();

        if (orderedIds.Count > 0)
            properties[$"{label}Ids"] = orderedIds;
    }
}
