using StarWarsData.Models.Entities;

namespace StarWarsData.Services.KnowledgeGraph.NodeBuilders.Types;

/// <summary>
/// <see cref="KgNodeTypes.ReferenceMagazine"/> extraction. Reference-style
/// periodicals (Star Wars Insider, Build the Millennium Falcon, etc.).
/// Phase C added <c>features</c> as the canonical edge label for the
/// <c>Featured</c> infobox field; the field-name → label mapping lives in
/// FieldSemantics, so this stub doesn't need an OnFinalize override.
/// </summary>
public sealed class ReferenceMagazineNodeBuilder : NodeBuilderBase
{
    public override string NodeType => KgNodeTypes.ReferenceMagazine;
}
