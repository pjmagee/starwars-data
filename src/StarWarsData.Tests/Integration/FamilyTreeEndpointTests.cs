using Microsoft.AspNetCore.Mvc;
using StarWarsData.ApiService.Controllers;
using StarWarsData.Models.Queries;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Design-042 Phase 3 — endpoint-level tests for
/// <c>GET /api/RelationshipGraph/family-tree/{pageId}</c>. Exercises the
/// controller's bind / validate / dispatch path directly (the test project
/// references ApiService so we can instantiate the controller with the same
/// <see cref="KnowledgeGraphQueryService"/> the app uses, against the
/// <see cref="ApiFixture"/> Testcontainers Mongo).
///
/// Spec: <c>specs/042-family-tree-component/contracts/family-tree-endpoint.md</c>.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class FamilyTreeEndpointTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await ApiFixture.EnsureInitializedAsync();

    private static RelationshipGraphController NewController() => new(ApiFixture.KnowledgeGraphQueryService);

    [TestMethod]
    public async Task GetFamilyTree_HappyPath_ReturnsCorrectWireShape()
    {
        var result = await NewController().FamilyTree(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "Expected 200 OK");
        var body = ok.Value as FamilyTreeResponse;
        Assert.IsNotNull(body);

        Assert.AreEqual(ApiFixture.AnakinPageId.ToString(), body.RootId);
        Assert.AreEqual("Anakin Skywalker", body.RootName);
        Assert.IsTrue(body.People.Count > 0);
        Assert.IsNotNull(body.Limitations, "Limitations block must always be present");

        // Root present.
        Assert.IsTrue(body.People.Any(p => p.Id == body.RootId));
    }

    [TestMethod]
    public async Task ContinuityPassthrough_CanonOnly_DropsLegendsOnlyEdges()
    {
        // Seed has Anakin → parent_of → Luke as Continuity.Legends. With
        // continuity=Canon the edge must be dropped, so Luke should no longer
        // be a *direct* child of Anakin in the result (but he might still
        // appear via Padmé → parent_of → Luke, which is Canon).
        var resultObj = await NewController().FamilyTree(ApiFixture.AnakinPageId, maxDepth: 3, continuity: "Canon", realm: null, ct: CancellationToken.None);

        var ok = resultObj as OkObjectResult;
        Assert.IsNotNull(ok);
        var body = (FamilyTreeResponse)ok.Value!;

        var anakin = body.People.SingleOrDefault(p => p.Id == ApiFixture.AnakinPageId.ToString());
        Assert.IsNotNull(anakin);

        // Anakin → Luke is Legends-only; under continuity=Canon it must NOT be
        // a direct child of Anakin (the Canon parent of Luke is Padmé).
        var lukeDirectlyAnakinChild = anakin.Rels.Children?.Contains(ApiFixture.LukePageId.ToString()) ?? false;
        Assert.IsFalse(lukeDirectlyAnakinChild, "continuity=Canon must drop the Legends-only Anakin → Luke edge from Anakin.Rels.Children");
    }

    [TestMethod]
    public async Task NonCharacterRoot_Returns400_WithRootMustBeCharacterBody()
    {
        // Organa family is type=Family, not Character.
        var result = await NewController().FamilyTree(ApiFixture.OrganaFamilyPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var bad = result as BadRequestObjectResult;
        Assert.IsNotNull(bad, "Expected 400 Bad Request for non-Character root");

        // Validate the body has the documented shape via reflection — the
        // anonymous object the controller returns isn't a strongly-typed
        // record, so reflection keeps the test independent of any internal
        // shape change.
        var body = bad.Value;
        Assert.IsNotNull(body);
        var errorProp = body.GetType().GetProperty("error");
        Assert.IsNotNull(errorProp);
        Assert.AreEqual("RootMustBeCharacter", errorProp.GetValue(body));

        var actualTypeProp = body.GetType().GetProperty("actualType");
        Assert.IsNotNull(actualTypeProp);
        Assert.AreEqual("Family", actualTypeProp.GetValue(body));
    }

    [TestMethod]
    public async Task UnknownRoot_Returns404_WithRootNotFoundBody()
    {
        var result = await NewController().FamilyTree(pageId: 999_999, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var notFound = result as NotFoundObjectResult;
        Assert.IsNotNull(notFound, "Expected 404 Not Found for unknown root");

        var body = notFound.Value;
        Assert.IsNotNull(body);
        var errorProp = body.GetType().GetProperty("error");
        Assert.IsNotNull(errorProp);
        Assert.AreEqual("RootNotFound", errorProp.GetValue(body));
    }

    [TestMethod]
    public async Task InvalidContinuity_Returns400_WithInvalidQueryParameterBody()
    {
        var result = await NewController().FamilyTree(ApiFixture.AnakinPageId, maxDepth: 3, continuity: "Wookies", realm: null, ct: CancellationToken.None);

        var bad = result as BadRequestObjectResult;
        Assert.IsNotNull(bad);
        var body = bad.Value;
        Assert.IsNotNull(body);
        var errorProp = body.GetType().GetProperty("error");
        Assert.AreEqual("InvalidQueryParameter", errorProp!.GetValue(body));
    }

    [TestMethod]
    public async Task BidirectionalInvariant_HoldsAcrossEndpointResponse()
    {
        var resultObj = await NewController().FamilyTree(ApiFixture.AnakinPageId, maxDepth: 3, continuity: null, realm: null, ct: CancellationToken.None);

        var ok = resultObj as OkObjectResult;
        Assert.IsNotNull(ok);
        var body = (FamilyTreeResponse)ok.Value!;

        // Walk the response (NOT trust the projection): every ID referenced in
        // any Rels.* MUST exist in People[]; spouse/parent/child symmetry MUST
        // hold. Mirrors invariant rule #4 in contracts/family-tree-endpoint.md.
        var byId = body.People.ToDictionary(p => p.Id);

        foreach (var p in body.People)
        {
            foreach (var s in p.Rels.Spouses ?? [])
            {
                Assert.IsTrue(byId.ContainsKey(s), $"Spouse ref {s} on {p.Id} not in People[]");
                Assert.IsTrue(byId[s].Rels.Spouses?.Contains(p.Id) ?? false, $"Spouse not reciprocated: {p.Id} ↔ {s}");
            }
            foreach (var par in p.Rels.Parents ?? [])
            {
                Assert.IsTrue(byId.ContainsKey(par), $"Parent ref {par} on {p.Id} not in People[]");
                Assert.IsTrue(byId[par].Rels.Children?.Contains(p.Id) ?? false, $"Child not reciprocated: {par} ↔ {p.Id}");
            }
            foreach (var ch in p.Rels.Children ?? [])
            {
                Assert.IsTrue(byId.ContainsKey(ch), $"Child ref {ch} on {p.Id} not in People[]");
                Assert.IsTrue(byId[ch].Rels.Parents?.Contains(p.Id) ?? false, $"Parent not reciprocated: {p.Id} ↔ {ch}");
            }
        }

        // Kinship endpoints must also be in People[] (invariant #5).
        foreach (var k in body.Kinship ?? [])
        {
            Assert.IsTrue(byId.ContainsKey(k.PersonId), $"Kinship PersonId {k.PersonId} not in People[]");
            Assert.IsTrue(byId.ContainsKey(k.RelativeId), $"Kinship RelativeId {k.RelativeId} not in People[]");
        }
    }
}
