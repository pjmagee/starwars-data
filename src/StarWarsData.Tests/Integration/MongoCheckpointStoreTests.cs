using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using StarWarsData.Services;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public class MongoCheckpointStoreTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await CheckpointStoreFixture.EnsureInitializedAsync();

    private static MongoCheckpointStore CreateStore() => new(CheckpointStoreFixture.Client, CheckpointStoreFixture.DbName);

    [TestMethod]
    public async Task CreateAndRetrieve_RoundTrips()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("""{"events": [1, 2, 3], "processed": true}""").RootElement;

        var info = await store.CreateCheckpointAsync(sessionId, payload, parent: null);

        Assert.AreEqual(sessionId, info.SessionId);
        Assert.IsFalse(string.IsNullOrEmpty(info.CheckpointId));

        var retrieved = await store.RetrieveCheckpointAsync(sessionId, info);

        Assert.AreEqual(3, retrieved.GetProperty("events").GetArrayLength());
        Assert.IsTrue(retrieved.GetProperty("processed").GetBoolean());
    }

    [TestMethod]
    public async Task CreateWithNullParent_DoesNotThrow()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var info = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        Assert.IsNotNull(info);
    }

    [TestMethod]
    public async Task CreateWithParent_StoresParentReference()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var parent = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        var child = await store.CreateCheckpointAsync(sessionId, payload, parent: parent);

        Assert.AreNotEqual(parent.CheckpointId, child.CheckpointId);

        var all = (await store.RetrieveIndexAsync(sessionId)).ToList();
        Assert.AreEqual(2, all.Count);

        var children = (await store.RetrieveIndexAsync(sessionId, withParent: parent)).ToList();
        Assert.AreEqual(1, children.Count);
        Assert.AreEqual(child.CheckpointId, children[0].CheckpointId);
    }

    [TestMethod]
    public async Task RetrieveCheckpoint_NotFound_ThrowsKeyNotFoundException()
    {
        var store = CreateStore();
        var bogus = new CheckpointInfo("no-session", "no-checkpoint");

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => store.RetrieveCheckpointAsync("no-session", bogus).AsTask());
    }

    [TestMethod]
    public async Task RetrieveIndex_EmptySession_ReturnsEmpty()
    {
        var store = CreateStore();

        var index = (await store.RetrieveIndexAsync($"empty-{Guid.NewGuid():N}")).ToList();

        Assert.AreEqual(0, index.Count);
    }

    [TestMethod]
    public async Task RetrieveIndex_MultipleCheckpoints_ReturnedInOrder()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var first = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        var second = await store.CreateCheckpointAsync(sessionId, payload, parent: first);
        var third = await store.CreateCheckpointAsync(sessionId, payload, parent: second);

        var index = (await store.RetrieveIndexAsync(sessionId)).ToList();

        Assert.AreEqual(3, index.Count);
        Assert.AreEqual(first.CheckpointId, index[0].CheckpointId);
        Assert.AreEqual(second.CheckpointId, index[1].CheckpointId);
        Assert.AreEqual(third.CheckpointId, index[2].CheckpointId);
    }

    [TestMethod]
    public async Task DifferentSessions_AreIsolated()
    {
        var store = CreateStore();
        var session1 = $"test-{Guid.NewGuid():N}";
        var session2 = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        await store.CreateCheckpointAsync(session1, payload, parent: null);
        await store.CreateCheckpointAsync(session1, payload, parent: null);
        await store.CreateCheckpointAsync(session2, payload, parent: null);

        var index1 = (await store.RetrieveIndexAsync(session1)).ToList();
        var index2 = (await store.RetrieveIndexAsync(session2)).ToList();

        Assert.AreEqual(2, index1.Count);
        Assert.AreEqual(1, index2.Count);
    }

    [TestMethod]
    public async Task ComplexPayload_PreservesStructure()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument
            .Parse(
                """
                {
                    "processedPageIds": [100, 200, 300],
                    "events": [
                        {
                            "eventType": "Battle",
                            "description": "Battle of Yavin",
                            "year": 0.0,
                            "demarcation": "ABY"
                        }
                    ]
                }
                """
            )
            .RootElement;

        var info = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        var retrieved = await store.RetrieveCheckpointAsync(sessionId, info);

        var pageIds = retrieved.GetProperty("processedPageIds");
        Assert.AreEqual(3, pageIds.GetArrayLength());
        Assert.AreEqual(100, pageIds[0].GetInt32());

        var events = retrieved.GetProperty("events");
        Assert.AreEqual(1, events.GetArrayLength());
        Assert.AreEqual("Battle", events[0].GetProperty("eventType").GetString());
    }

    [TestMethod]
    public async Task ClearSession_RemovesAllCheckpoints()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        await store.CreateCheckpointAsync(sessionId, payload, parent: null);

        await store.ClearSessionAsync(sessionId);

        var index = (await store.RetrieveIndexAsync(sessionId)).ToList();
        Assert.AreEqual(0, index.Count);
    }

    [TestMethod]
    public async Task ClearSession_DoesNotAffectOtherSessions()
    {
        var store = CreateStore();
        var session1 = $"test-{Guid.NewGuid():N}";
        var session2 = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        await store.CreateCheckpointAsync(session1, payload, parent: null);
        await store.CreateCheckpointAsync(session2, payload, parent: null);

        await store.ClearSessionAsync(session1);

        Assert.AreEqual(0, (await store.RetrieveIndexAsync(session1)).Count());
        Assert.AreEqual(1, (await store.RetrieveIndexAsync(session2)).Count());
    }
}
