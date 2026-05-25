using StarWarsData.Models.Queries;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Design-042 Phase 3 — projection-level tests for
/// <see cref="KnowledgeGraphQueryService.BuildFamilyTreeAsync"/>. The data-model
/// edge-case matrix in <c>specs/042-family-tree-component/data-model.md § Edge cases</c>
/// drives one test per row.
///
/// Tier choice: these tests depend on <see cref="ApiFixture"/> (Testcontainers
/// MongoDB), so per the existing repo convention they're <see cref="TestTiers.Integration"/>.
/// The plan's "Unit" label for projection tests reflects the conceptual scope —
/// no LLM, no live app — but Principle III binds the tier to the actual
/// infrastructure cost. The plan's own Testing block hedges with "ApiFixture
/// seed" so an Integration tier classification is the closest honest mapping.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class FamilyTreeProjectionTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await ApiFixture.EnsureInitializedAsync();

    private static KnowledgeGraphQueryService Svc => ApiFixture.KnowledgeGraphQueryService;

    [TestMethod]
    public async Task Anakin_Root_HasExpectedParentsSpousesChildren()
    {
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        Assert.AreEqual(ApiFixture.AnakinPageId.ToString(), result.RootId);
        Assert.AreEqual("Anakin Skywalker", result.RootName);

        var anakin = result.People.FirstOrDefault(p => p.Id == ApiFixture.AnakinPageId.ToString());
        Assert.IsNotNull(anakin, "Anakin must be in People[]");

        CollectionAssert.Contains(anakin.Rels.Spouses?.ToList(), ApiFixture.PadmePageId.ToString());
        CollectionAssert.Contains(anakin.Rels.Parents?.ToList(), ApiFixture.ShmiPageId.ToString());
        CollectionAssert.Contains(anakin.Rels.Children?.ToList(), ApiFixture.LukePageId.ToString());
        CollectionAssert.Contains(anakin.Rels.Children?.ToList(), ApiFixture.LeiaPageId.ToString());
    }

    [TestMethod]
    public async Task BidirectionalSpouseRepair_HoldsForOneSidedPartnerOf()
    {
        // Seed has only (Anakin, partner_of, Padmé) — no reverse. Repair MUST
        // surface the link on BOTH Anakin.Spouses AND Padmé.Spouses.
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var anakin = result.People.Single(p => p.Id == ApiFixture.AnakinPageId.ToString());
        var padme = result.People.Single(p => p.Id == ApiFixture.PadmePageId.ToString());

        CollectionAssert.Contains(anakin.Rels.Spouses?.ToList(), ApiFixture.PadmePageId.ToString());
        CollectionAssert.Contains(padme.Rels.Spouses?.ToList(), ApiFixture.AnakinPageId.ToString());
    }

    [TestMethod]
    public async Task ParentChildSymmetry_IsEnforced()
    {
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        // For every (A, parents=[B]), assert B exists and B.children contains A.
        foreach (var person in result.People)
        {
            if (person.Rels.Parents is null)
                continue;
            foreach (var parentId in person.Rels.Parents)
            {
                var parent = result.People.SingleOrDefault(p => p.Id == parentId);
                Assert.IsNotNull(parent, $"Parent {parentId} of {person.Id} must be in People[]");
                Assert.IsNotNull(parent.Rels.Children, $"Parent {parentId} must have a Children list");
                CollectionAssert.Contains(parent.Rels.Children, person.Id, $"Symmetry broken: {parent.Id}.Children must include {person.Id}");
            }
        }
    }

    [TestMethod]
    public async Task Truncation_EmitsSyntheticStubsWithRealPageId()
    {
        // Force truncation by clamping maxNodes very low. We pass maxNodes=2 so
        // the BFS visits at most root + 1 neighbour; the rest become synthetic
        // stubs that should still carry their REAL PageId on Data.PageId per R-7.
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, maxNodes: 2, ct: CancellationToken.None);

        Assert.IsTrue(result.Limitations.TruncatedAtDepth, "TruncatedAtDepth must be set when maxNodes is hit");

        var stubs = result.People.Where(p => p.Id.EndsWith("-stub", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(stubs.Count > 0, "Truncated BFS must emit at least one synthetic stub for the referenced-but-not-visited nodes");

        foreach (var stub in stubs)
        {
            Assert.IsTrue(stub.Data.PageId > 0, "Stub must carry the real PageId so the click handler can navigate");
            Assert.AreEqual($"{stub.Data.PageId}-stub", stub.Id, "Stub Id must follow the {pageId}-stub convention");
        }
    }

    [TestMethod]
    public async Task MissingGender_DefaultsToM_AndPopulatesLimitations()
    {
        // Han Solo has no Gender field in raw.pages.infobox (data-model.md edge case #7).
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        // Han is visited via Leia ↔ spouse_of edge (one extra hop from Anakin).
        var han = result.People.FirstOrDefault(p => p.Id == ApiFixture.HanSoloPageId.ToString());
        Assert.IsNotNull(han, "Han Solo should be visited at maxDepth=3 from Anakin via Leia's spouse edge");
        Assert.AreEqual("M", han.Data.Gender, "Missing Gender must default to M");
        CollectionAssert.Contains(result.Limitations.MissingGenders, ApiFixture.HanSoloPageId, "Han's PageId must appear in Limitations.MissingGenders");
    }

    [TestMethod]
    public async Task FamilyMembership_DoesNotBecomeParentRelation()
    {
        // Leia ↔ Organa family AND Bail ↔ Organa family, but NO parent_of Bail→Leia.
        // The family-membership co-occurrence must NOT translate into Leia.Rels.Parents.
        //
        // Earlier draft also asserted that an entry surfaced in
        // Limitations.AdoptiveRelationsExcluded. That heuristic was structurally
        // over-broad (every pair of family members lacking a direct parent_of
        // gets flagged, including siblings/spouses/grandparents/in-laws — 200+
        // false positives on real starwars-dev data), so the projection no
        // longer populates AdoptiveRelationsExcluded. Without explicit
        // `adopted_by` edge labels in the KG (Design-042 § Revisit when),
        // there is no reliable rule for detecting true adoption from
        // infobox-only data. Family edges are dropped silently.
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var leia = result.People.Single(p => p.Id == ApiFixture.LeiaPageId.ToString());
        var bailId = ApiFixture.BailOrganaPageId.ToString();

        if (leia.Rels.Parents is { } parents)
            Assert.IsFalse(parents.Contains(bailId), "Bail must NOT appear in Leia.Rels.Parents — family-membership edges don't translate");

        Assert.AreEqual(0, result.Limitations.AdoptiveRelationsExcluded.Count, "AdoptiveRelationsExcluded must stay empty — the heuristic was over-broad and is no longer populated");
    }

    [TestMethod]
    public async Task HasRelative_AppearsInKinshipOnly_NeverInRels()
    {
        // Seed has (Luke, has_relative, Ben Solo, qualifier="Nephew").
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.LukePageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        Assert.IsNotNull(result.Kinship, "Kinship list must be present when has_relative edges exist");
        Assert.IsTrue(
            result.Kinship!.Any(k => k.PersonId == ApiFixture.LukePageId.ToString() && k.RelativeId == ApiFixture.BenSoloPageId.ToString() && k.Relationship == "Nephew"),
            "has_relative edge with qualifier=Nephew must produce a Kinship[] entry"
        );

        var luke = result.People.Single(p => p.Id == ApiFixture.LukePageId.ToString());
        var benIdStr = ApiFixture.BenSoloPageId.ToString();
        Assert.IsFalse(luke.Rels.Parents?.Contains(benIdStr) ?? false, "has_relative must not surface in Parents");
        Assert.IsFalse(luke.Rels.Spouses?.Contains(benIdStr) ?? false, "has_relative must not surface in Spouses");
        Assert.IsFalse(luke.Rels.Children?.Contains(benIdStr) ?? false, "has_relative must not surface in Children");
    }

    [TestMethod]
    public async Task MaxDepth_IsClampedToOneFiveBoundary()
    {
        // Out-of-range values should clamp silently inside BuildFamilyTreeAsync —
        // we assert by observing that the projection still succeeds (no exception)
        // and produces a usable result.
        var clampLow = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 0, continuity: null, realm: null, ct: CancellationToken.None);
        Assert.IsTrue(clampLow.People.Count > 0, "maxDepth=0 must clamp to 1 and still project the root + direct neighbours");

        var clampNegative = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: -1, continuity: null, realm: null, ct: CancellationToken.None);
        Assert.IsTrue(clampNegative.People.Count > 0, "maxDepth=-1 must clamp to 1");

        var clampHigh = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 99, continuity: null, realm: null, ct: CancellationToken.None);
        Assert.IsTrue(clampHigh.People.Count > 0, "maxDepth=99 must clamp to 5 and still project");
        // At depth=5 from Anakin we should reach Ben Solo (Anakin → Leia → Han spouse → Ben).
        Assert.IsTrue(clampHigh.People.Any(p => p.Id == ApiFixture.BenSoloPageId.ToString()), "Clamped maxDepth=5 should reach Ben Solo via Anakin → Leia → Ben");
    }

    [TestMethod]
    public async Task RootPresent_BidirectionalInvariant_HoldsAcrossWholeResult()
    {
        // Walks the entire projection and asserts every Rels.* ID points at an
        // entry in People[] (real or stub), and that every spouse / parent / child
        // edge is reciprocated. Covers data-model.md § Validation rules in one
        // shot — supplementing the per-edge-case tests above.
        var result = await Svc.BuildFamilyTreeAsync(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        Assert.IsTrue(result.People.Any(p => p.Id == result.RootId), "Root must be present in People[]");

        var ids = result.People.Select(p => p.Id).ToHashSet();
        foreach (var p in result.People)
        {
            AssertAllReferenced(p.Rels.Parents, ids, $"{p.Id}.Parents");
            AssertAllReferenced(p.Rels.Spouses, ids, $"{p.Id}.Spouses");
            AssertAllReferenced(p.Rels.Children, ids, $"{p.Id}.Children");
        }

        var byId = result.People.ToDictionary(p => p.Id);
        foreach (var p in result.People)
        {
            if (p.Rels.Spouses is { } spouses)
                foreach (var s in spouses)
                    Assert.IsTrue(byId[s].Rels.Spouses?.Contains(p.Id) ?? false, $"Bidirectional spouse violation: {p.Id} ↔ {s}");
            if (p.Rels.Parents is { } parents)
                foreach (var par in parents)
                    Assert.IsTrue(byId[par].Rels.Children?.Contains(p.Id) ?? false, $"Parent/child symmetry violation: {p.Id} parent {par}");
        }
    }

    private static void AssertAllReferenced(List<string>? refs, HashSet<string> ids, string context)
    {
        if (refs is null)
            return;
        foreach (var r in refs)
            Assert.IsTrue(ids.Contains(r), $"{context}: id '{r}' not in People[]");
    }
}
