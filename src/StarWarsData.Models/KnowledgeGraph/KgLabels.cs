namespace StarWarsData.Models.Entities;

/// <summary>
/// Canonical KG edge labels stored in <see cref="RelationshipEdge.Label"/>.
/// These match the values emitted by the ETL and the label keys in
/// <c>FieldSemantics.Relationships</c>. Use these constants at call sites
/// instead of scattered string literals so a rename is a single-point change.
/// </summary>
/// <remarks>
/// This is intentionally NOT exhaustive — it holds labels referenced from C#
/// code (services, toolkits, ETL passes). Labels used only in data / prompts
/// are looked up through FieldSemantics by name.
/// </remarks>
public static class KgLabels
{
    // ── Affiliation / membership ──
    public const string MemberOf = "member_of";
    public const string AffiliatedWith = "affiliated_with";
    public const string FoundedBy = "founded_by";
    public const string HeadOfState = "head_of_state";
    public const string HasCapital = "has_capital";

    // ── Family ──
    public const string ChildOf = "child_of";
    public const string ParentOf = "parent_of";
    public const string SiblingOf = "sibling_of";
    public const string PartnerOf = "partner_of";

    // ── Mentorship ──
    public const string ApprenticeOf = "apprentice_of";
    public const string MasterOf = "master_of";
    public const string Trained = "trained";
    public const string TrainedBy = "trained_by";

    // ── Origin / biology ──
    public const string Species = "species";
    public const string Homeworld = "homeworld";
    public const string OriginatesFrom = "originates_from";

    // ── Military / conflict ──
    public const string FoughtIn = "fought_in";
    public const string Belligerent = "belligerent";
    public const string CommandedBy = "commanded_by";
    public const string Commanded = "commanded";
    public const string BattleIn = "battle_in";
    public const string TookPlaceAt = "took_place_at";
    public const string Participant = "participant";

    // ── Geography ──
    public const string InRegion = "in_region";
    public const string LocatedIn = "located_in";

    // ── Construction / manufacture ──
    public const string Built = "built";
    public const string ManufacturedBy = "manufactured_by";

    // ── Trade routes ──
    public const string EndPoints = "end_points";
    public const string TransitPoints = "transit_points";
    public const string HasObject = "has_object";
    public const string Junctions = "junctions";
    public const string Regions = "regions";
}
