using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StarWarsData.Services;

/// <summary>
/// MongoDB-backed checkpoint store for workflow execution state.
/// Enables resume-on-failure: if extraction dies at page 20/36,
/// the next run restores from the last checkpoint and continues.
/// </summary>
public sealed class MongoCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoCheckpointStore(IMongoClient mongoClient, string databaseName)
    {
        _collection = mongoClient
            .GetDatabase(databaseName)
            .GetCollection<BsonDocument>("WorkflowCheckpoints");

        // Ensure index on sessionId for fast lookups
        _collection.Indexes.CreateOne(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("sessionId")
            )
        );
    }

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(
        string sessionId,
        JsonElement value,
        CheckpointInfo? parent
    )
    {
        var checkpointId = Guid.NewGuid().ToString("N");
        var info = new CheckpointInfo(sessionId, checkpointId);

        var doc = new BsonDocument
        {
            ["_id"] = $"{sessionId}|{checkpointId}",
            ["sessionId"] = sessionId,
            ["checkpointId"] = checkpointId,
            ["parentCheckpointId"] = parent?.CheckpointId is string pid
                ? (BsonValue)pid
                : BsonNull.Value,
            ["value"] = BsonDocument.Parse(value.GetRawText()),
            ["createdAt"] = DateTime.UtcNow,
        };

        await _collection.InsertOneAsync(doc);
        return info;
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(
        string sessionId,
        CheckpointInfo key
    )
    {
        var docId = $"{sessionId}|{key.CheckpointId}";
        var doc = await _collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", docId))
            .FirstOrDefaultAsync();

        if (doc is null)
            throw new KeyNotFoundException($"Checkpoint not found: {docId}");

        var json = doc["value"].ToJson();
        return JsonDocument.Parse(json).RootElement;
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
        string sessionId,
        CheckpointInfo? withParent = null
    )
    {
        var filter = Builders<BsonDocument>.Filter.Eq("sessionId", sessionId);

        if (withParent is not null)
            filter &= Builders<BsonDocument>.Filter.Eq(
                "parentCheckpointId",
                withParent.CheckpointId
            );

        var docs = await _collection.Find(filter).SortBy(d => d["createdAt"]).ToListAsync();

        return docs.Select(d => new CheckpointInfo(
                d["sessionId"].AsString,
                d["checkpointId"].AsString
            ))
            .ToList();
    }

    /// <summary>
    /// Delete all checkpoints for a session (cleanup after successful completion or before regeneration).
    /// </summary>
    public async Task ClearSessionAsync(string sessionId)
    {
        await _collection.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("sessionId", sessionId));
    }
}
