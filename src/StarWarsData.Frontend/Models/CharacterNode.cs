using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Frontend.Models;

public class CharacterNode : NodeModel
{
    public FamilyNodeDto Character { get; }

    public CharacterNode(FamilyNodeDto character, Point? position = null)
        : base(position)
    {
        Character = character;
    }
}
