namespace StarWarsData.Models.Entities;

/// <summary>
/// Represents whether content is in-universe (fictional) or out-of-universe (real-world/meta)
/// </summary>
public enum Universe
{
    /// <summary>
    /// In-universe content — fictional, from within the Star Wars universe
    /// </summary>
    InUniverse = 0,

    /// <summary>
    /// Out-of-universe content — real-world articles, behind-the-scenes, meta, canceled projects, etc.
    /// </summary>
    OutOfUniverse = 1,

    /// <summary>
    /// Unknown or unspecified
    /// </summary>
    Unknown = 2,
}
