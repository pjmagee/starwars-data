using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using MongoDB.Driver;
using StarWarsData.Services;
using Testcontainers.MongoDb;

namespace StarWarsData.Tests;

public sealed class CheckpointStoreFixture : IAsyncLifetime
{
    private MongoDbContainer _container = null!;
    public IMongoClient Client { get; private set; } = null!;

    public const string DbName = "test-checkpoints";

    public async Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8").Build();

        await _container.StartAsync();
        Client = new MongoClient(_container.GetConnectionString());
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition("Checkpoints")]
public class CheckpointCollection : ICollectionFixture<CheckpointStoreFixture> { }

[Collection("Checkpoints")]
public class MongoCheckpointStoreTests(CheckpointStoreFixture fixture)
{
    private MongoCheckpointStore CreateStore() =>
        new(fixture.Client, CheckpointStoreFixture.DbName);

    [Fact]
    public async Task CreateAndRetrieve_RoundTrips()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument
            .Parse("""{"events": [1, 2, 3], "processed": true}""")
            .RootElement;

        var info = await store.CreateCheckpointAsync(sessionId, payload, parent: null);

        Assert.Equal(sessionId, info.SessionId);
        Assert.False(string.IsNullOrEmpty(info.CheckpointId));

        var retrieved = await store.RetrieveCheckpointAsync(sessionId, info);

        Assert.Equal(3, retrieved.GetProperty("events").GetArrayLength());
        Assert.True(retrieved.GetProperty("processed").GetBoolean());
    }

    [Fact]
    public async Task CreateWithNullParent_DoesNotThrow()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var info = await store.CreateCheckpointAsync(sessionId, payload, parent: null);

        Assert.NotNull(info);
    }

    [Fact]
    public async Task CreateWithParent_StoresParentReference()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var parent = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        var child = await store.CreateCheckpointAsync(sessionId, payload, parent: parent);

        Assert.NotEqual(parent.CheckpointId, child.CheckpointId);

        // Both should appear in the full index
        var all = (await store.RetrieveIndexAsync(sessionId)).ToList();
        Assert.Equal(2, all.Count);

        // Filtering by parent should return only the child
        var children = (await store.RetrieveIndexAsync(sessionId, withParent: parent)).ToList();
        Assert.Single(children);
        Assert.Equal(child.CheckpointId, children[0].CheckpointId);
    }

    [Fact]
    public async Task RetrieveCheckpoint_NotFound_ThrowsKeyNotFoundException()
    {
        var store = CreateStore();
        var bogus = new CheckpointInfo("no-session", "no-checkpoint");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.RetrieveCheckpointAsync("no-session", bogus).AsTask()
        );
    }

    [Fact]
    public async Task RetrieveIndex_EmptySession_ReturnsEmpty()
    {
        var store = CreateStore();

        var index = (await store.RetrieveIndexAsync($"empty-{Guid.NewGuid():N}")).ToList();

        Assert.Empty(index);
    }

    [Fact]
    public async Task RetrieveIndex_MultipleCheckpoints_ReturnedInOrder()
    {
        var store = CreateStore();
        var sessionId = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        var first = await store.CreateCheckpointAsync(sessionId, payload, parent: null);
        var second = await store.CreateCheckpointAsync(sessionId, payload, parent: first);
        var third = await store.CreateCheckpointAsync(sessionId, payload, parent: second);

        var index = (await store.RetrieveIndexAsync(sessionId)).ToList();

        Assert.Equal(3, index.Count);
        Assert.Equal(first.CheckpointId, index[0].CheckpointId);
        Assert.Equal(second.CheckpointId, index[1].CheckpointId);
        Assert.Equal(third.CheckpointId, index[2].CheckpointId);
    }

    [Fact]
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

        Assert.Equal(2, index1.Count);
        Assert.Single(index2);
    }

    [Fact]
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
        Assert.Equal(3, pageIds.GetArrayLength());
        Assert.Equal(100, pageIds[0].GetInt32());

        var events = retrieved.GetProperty("events");
        Assert.Single(events.EnumerateArray());
        Assert.Equal("Battle", events[0].GetProperty("eventType").GetString());
    }

    [Fact]
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
        Assert.Empty(index);
    }

    [Fact]
    public async Task ClearSession_DoesNotAffectOtherSessions()
    {
        var store = CreateStore();
        var session1 = $"test-{Guid.NewGuid():N}";
        var session2 = $"test-{Guid.NewGuid():N}";
        var payload = JsonDocument.Parse("{}").RootElement;

        await store.CreateCheckpointAsync(session1, payload, parent: null);
        await store.CreateCheckpointAsync(session2, payload, parent: null);

        await store.ClearSessionAsync(session1);

        Assert.Empty((await store.RetrieveIndexAsync(session1)).ToList());
        Assert.Single((await store.RetrieveIndexAsync(session2)).ToList());
    }
}
