namespace StarWarsData.Models.Entities;

/// <summary>
/// Provenance of an edge's <c>fromYear</c> / <c>toYear</c> values. Tagged at
/// write time by Phase 5 so downstream code (Holocron FillGap pre-flight, query-
/// time merge layers) can distinguish hard infobox-supplied bounds from soft
/// lifecycle-fallback derivations and treat them appropriately. Persisted on
/// <see cref="EdgeMeta.BoundsSource"/>. See Design-021.
/// </summary>
public enum EdgeBoundsSource
{
    /// <summary>
    /// Default — bounds are unset (both <c>fromYear</c> and <c>toYear</c> are null) or
    /// provenance was never recorded. Treated the same as <see cref="Lifecycle"/> for
    /// the read-side merge overlay (i.e. refinable) since we have no evidence the
    /// bound is hard.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The infobox value carried explicit years (e.g. <c>"Sith Lord (19 BBY – 4 ABY)"</c>).
    /// Bounds are evidence-backed by the source wiki text. Hard — Holocron <c>FillGap</c>
    /// will not overwrite these.
    /// </summary>
    Infobox = 1,

    /// <summary>
    /// Phase 5 derived the bound from endpoint lifecycle intersection
    /// (<c>max(start)</c>, <c>min(end)</c> across the two endpoints). Correct as an
    /// upper bound but typically loose for relationships that are sub-spans of
    /// either endpoint's life. Soft — Holocron <c>FillGap</c> may refine these
    /// when chunks cite tighter bounds.
    /// </summary>
    Lifecycle = 2,

    /// <summary>
    /// A Holocron run refined the bound. Set on the <c>kg.edge_enrichments</c> value,
    /// not on the base edge — included here for parity and to allow future code to
    /// stamp the base edge if we ever materialise refined bounds back into <c>kg.edges</c>.
    /// </summary>
    Holocron = 3,
}
