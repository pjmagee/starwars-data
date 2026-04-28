using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.IuMedia"/> extraction. Wookieepedia's "in-universe
/// media" template — fictional books, holos, songs, and broadcasts within
/// Star Wars itself. The type-name string is <c>"IU_media"</c> (with
/// underscore); the C# constant <c>IuMedia</c> uses PascalCase per identifier
/// convention. No type-specific logic yet.
/// </summary>
public sealed class IuMediaNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.IuMedia;
}
