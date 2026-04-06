namespace StarWarsData.Models.Queries;

/// <summary>
/// Calendar system a <see cref="Entities.TimelineEvent"/> is plotted against.
/// Galactic events carry a <c>Year</c> magnitude and a <c>Demarcation</c> (BBY/ABY).
/// Real events carry a signed CE <c>RealYear</c> (negative = BCE) and ignore Demarcation.
/// </summary>
public enum Calendar
{
    Galactic,
    Real,
    Unknown,
}
