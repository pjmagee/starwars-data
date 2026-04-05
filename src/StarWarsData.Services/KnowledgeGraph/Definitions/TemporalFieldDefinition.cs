namespace StarWarsData.Services.KnowledgeGraph.Definitions;

/// <summary>
/// Metadata describing a temporal infobox field — maps an infobox label
/// (e.g. "Date founded") to a semantic temporal dimension and calendar hint.
/// </summary>
/// <param name="Semantic">
/// Dot-separated dimension and role — for point fields this is the full
/// <c>dimension.role</c> pair (e.g. <c>"lifespan.start"</c>, <c>"institutional.reorganized"</c>,
/// <c>"publication.release"</c>). For <see cref="IsRange"/> fields this is the
/// dimension only (e.g. <c>"era"</c>); the extractor appends <c>.start</c>, <c>.end</c>,
/// or <c>.point</c> based on the number of years found in the value.
/// </param>
/// <param name="Calendar">
/// Calendar hint: "galactic" (BBY/ABY), "real" (CE), or "auto" (detect from text).
/// </param>
/// <param name="IsRange">
/// When true, a single value string is expected to contain multiple year occurrences
/// forming a range (e.g. <c>"25,000 BBY – 19 BBY"</c> on the <c>Era.Years</c> field).
/// The extractor matches every <c>BBY</c>/<c>ABY</c> token in the value and emits one
/// <see cref="StarWarsData.Models.Entities.TemporalFacet"/> per year, sorted chronologically.
/// </param>
public sealed record TemporalFieldDefinition(string Semantic, string Calendar, bool IsRange = false);
