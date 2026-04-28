using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.Organization"/> extraction. Non-government groups
/// (corporations, criminal syndicates, religious orders).
///
/// <para>Design-024 Phase C — C6: drop <c>led_by</c> edges where the target is
/// another Organization. The survey found 64 such edges on starwars-dev — they
/// are MediaWiki-link parser noise (an organisation can't be "led by" another
/// organisation; a person leads, and the person's name is what should appear).
/// Filter pattern only — no relabel, no salvage attempt; the generic loop
/// produces these from "Leader(s)" rows whose value is a multi-link string
/// where the parser surfaced the wrong primary entity.</para>
/// </summary>
public sealed class OrganizationNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.Organization;

    protected override void OnFinalize(NodeBuilderContext ctx, GraphNode node, List<RelationshipEdge> edges)
    {
        for (var i = edges.Count - 1; i >= 0; i--)
        {
            var edge = edges[i];
            if (!string.Equals(edge.Label, "led_by", StringComparison.OrdinalIgnoreCase))
                continue;

            var targetType = ctx.NodeTypeByPageId.GetValueOrDefault(edge.ToId, "");
            if (string.Equals(targetType, KgNodeTypes.Organization, StringComparison.OrdinalIgnoreCase))
            {
                edges.RemoveAt(i);
            }
        }
    }
}
