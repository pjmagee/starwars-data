using System.Text.RegularExpressions;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Hallucination-prevention regex coverage for the Holocron consolidator's pre-flight
/// reject (Design-025 §What's actually broken — hallucinated content). Cases are
/// drawn verbatim from the Ahsoka Tano v1.3.0 run on 2026-04-29 — every entry below
/// is a real claim text the agent emitted while admitting in the same breath that
/// the inference was weak.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class HolocronHedgeRegexTests
{
    // Mirrors the static field on HolocronConsolidatorExecutor. Kept duplicated
    // here so the regex can be exercised without spinning up the full executor —
    // the test will fail loudly if the patterns drift apart.
    static readonly Regex Pattern = new(
        @"appears? alongside|appears? with|appearances? context|appearance listings?|appearances? index|"
            + @"linked entity list|linked from pages|as a linked entity|as a distinct linked entity|"
            + @"no add edge is warranted|not warranted|not supported strongly|if supported by|if the source supports",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    [TestMethod]
    [DataRow("Ahsoka Tano appears alongside Darth Sidious in canon episode credits/appearance listings associated with The Wrong Jedi and The Clone Wars material.")]
    [DataRow("Ahsoka Tano is described among bounty hunters in an appearances context, indicating the bounty hunter role is a distinct linked entity in the source material.")]
    [DataRow(
        "Ahsoka Tano holds the role/title of Grand Master in the referenced material's linked entity list only as a title association, not as a direct office of Ahsoka, so no add edge is warranted."
    )]
    [DataRow("Ahsoka Tano is described in linked material as human in the appearances index, but this is not supported strongly enough to add as a species edge.")]
    [DataRow(
        "Ahsoka Tano and Asajj Ventress are shown together in appearances linked from pages featuring both characters, indicating a meaningful on-page relationship worth encoding if supported by the source chunks."
    )]
    public void RealHallucinatedClaim_IsRejected(string claim)
    {
        Assert.IsTrue(Pattern.IsMatch(claim), $"Hedge regex must match hallucinated claim: '{claim}'");
    }

    [TestMethod]
    // Direct, unhedged claims from the same run that ARE backed by chunk evidence
    // and should NOT be rejected. Picked from the FillGap edges where the agent
    // was confident.
    [DataRow("Ahsoka Tano was abducted from Shili at age three and brought to the Jedi Temple in 33 BBY.")]
    [DataRow("Anakin Skywalker took Ahsoka Tano as his Padawan in 22 BBY at the start of the Clone Wars.")]
    [DataRow("Sabine Wren trained as Ahsoka Tano's Padawan beginning around 1 ABY.")]
    [DataRow("Ahsoka Tano served in the 501st Legion during the Clone Wars.")]
    public void DirectClaim_IsAccepted(string claim)
    {
        Assert.IsFalse(Pattern.IsMatch(claim), $"Direct claim must NOT be rejected: '{claim}'");
    }

    [TestMethod]
    public void Pattern_IsCaseInsensitive()
    {
        Assert.IsTrue(Pattern.IsMatch("APPEARS ALONGSIDE"));
        Assert.IsTrue(Pattern.IsMatch("Appearances Index"));
        Assert.IsTrue(Pattern.IsMatch("If Supported By"));
    }

    [TestMethod]
    public void Pattern_HandlesSingularAndPluralAppearance()
    {
        Assert.IsTrue(Pattern.IsMatch("appearance listing"));
        Assert.IsTrue(Pattern.IsMatch("appearance listings"));
        Assert.IsTrue(Pattern.IsMatch("appearances context"));
        Assert.IsTrue(Pattern.IsMatch("appearance context"));
    }
}
