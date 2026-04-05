namespace StarWarsData.Models.Entities;

/// <summary>
/// Flat DTO for a knowledge-graph relationship edge produced by the builder pipeline
/// and consumed by the <c>RelationshipAnalystToolkit</c>. Contains the denormalized
/// names and types of both endpoints plus the reverse label for bidirectional display.
/// </summary>
public class EdgeDto
{
    public int FromId { get; set; }
    public string FromName { get; set; } = "";
    public string FromType { get; set; } = "";
    public int ToId { get; set; }
    public string ToName { get; set; } = "";
    public string ToType { get; set; } = "";
    public string Label { get; set; } = "";
    public string ReverseLabel { get; set; } = "";
    public double Weight { get; set; }
    public string Evidence { get; set; } = "";
    public string Continuity { get; set; } = "Unknown";
}
