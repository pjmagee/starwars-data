using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using StarWarsData.Models.Queries;

namespace StarWarsData.Client.Models;

public class CharacterNode : NodeModel
{
    public CharacterRelationsDto Character { get; }

    public CharacterNode(CharacterRelationsDto character, Point? position = null) : base(position)
    {
        Character = character;
    }
}