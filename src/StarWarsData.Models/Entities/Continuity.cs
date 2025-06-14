namespace StarWarsData.Models.Entities;

/// <summary>
/// Represents the continuity classification for Star Wars content
/// </summary>
public enum Continuity
{
    /// <summary>
    /// Canon content officially recognized by Disney/Lucasfilm
    /// </summary>
    Canon = 0,

    /// <summary>
    /// Legends content (formerly Expanded Universe)
    /// </summary>
    Legends = 1,

    /// <summary>
    /// Both canon and legends content
    /// </summary>
    Both = 2,

    /// <summary>
    /// Unknown or unspecified continuity
    /// </summary>
    Unknown = 3,
}
