namespace StarWarsData.Models.Entities;

/// <summary>
/// Represents the realm a page or event belongs to.
/// Star Wars = fictional in-universe content. Real = real-world/meta content
/// (authors, actors, publications, behind-the-scenes, releases). Pairs with
/// the BBY/ABY vs Gregorian date duality.
/// </summary>
public enum Realm
{
    /// <summary>
    /// Fictional content from within the Star Wars universe.
    /// </summary>
    Starwars = 0,

    /// <summary>
    /// Real-world content — actors, authors, publications, filming locations,
    /// behind-the-scenes, releases, canceled projects, meta articles.
    /// </summary>
    Real = 1,

    /// <summary>
    /// Unclassified. Used as a safety-net bucket so unclassified content
    /// is never silently hidden by the realm filter.
    /// </summary>
    Unknown = 2,
}
