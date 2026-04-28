using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Tests.Infrastructure;

namespace StarWarsData.Tests.Integration;

/// <summary>
/// Regression coverage for migration <c>0014-holocron-jobid-unique-indexes</c> — the
/// partial unique indexes that make <c>HolocronApplyExecutor</c> idempotent under
/// crash-and-resume (Design-020 risk #4).
///
/// The first version of this migration used <c>$ne: ""</c> in the partial filter
/// expression, which MongoDB rejects with "Expression not supported in partial index:
/// $not". The bug only surfaced when applying to a real cluster — unit tests against
/// the JS file wouldn't have caught it. These tests run the same partial-filter
/// expression against a Testcontainers Mongo so the C# regression suite catches any
/// future drift between the migration syntax and what the server actually accepts.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Integration)]
[DoNotParallelize]
public sealed class HolocronJobIdUniqueIndexTests
{
    [ClassInitialize]
    public static async Task ClassSetup(TestContext _) => await CheckpointStoreFixture.EnsureInitializedAsync();

    /// <summary>
    /// Use a fresh DB name per test so the indexes from one test don't bleed into the next —
    /// CheckpointStoreFixture's primary DB is reused by other tests.
    /// </summary>
    static IMongoDatabase NewDb()
    {
        var dbName = $"holocron-idx-{Guid.NewGuid():N}";
        return CheckpointStoreFixture.Client.GetDatabase(dbName);
    }

    /// <summary>
    /// Mongo's partial filter expressions support a limited operator set: <c>$exists</c>,
    /// <c>$eq</c>, <c>$gt/gte/lt/lte</c>, <c>$type</c>, <c>$and</c>, <c>$or</c> — NOT
    /// <c>$ne</c>. Migration 0014 uses <c>{$type: "string", $gt: ""}</c> as the
    /// equivalent legacy-row exemption. This test asserts that exact filter shape is
    /// accepted by the server.
    /// </summary>
    [TestMethod]
    public async Task NodeEnrichmentPartialUniqueIndex_AcceptsTypeAndGtFilter()
    {
        var db = NewDb();
        var coll = db.GetCollection<BsonDocument>("kg.enrichments");

        var partialFilter = new BsonDocument("jobId", new BsonDocument { ["$type"] = "string", ["$gt"] = "" });

        var keys = new BsonDocument
        {
            ["jobId"] = 1,
            ["pageId"] = 1,
            ["fieldPath"] = 1,
        };

        var name = await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                keys,
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "jobId_1_pageId_1_fieldPath_1_unique",
                    Unique = true,
                    PartialFilterExpression = partialFilter,
                }
            )
        );

        Assert.AreEqual("jobId_1_pageId_1_fieldPath_1_unique", name);
    }

    /// <summary>
    /// Two writes with the same <c>(jobId, pageId, fieldPath)</c> triple are rejected
    /// with E11000. This is the load-bearing replay safety net — a partial-Apply
    /// followed by a re-Apply can't double-write enrichments.
    /// </summary>
    [TestMethod]
    public async Task NodeEnrichmentPartialUniqueIndex_RejectsDuplicateOnSameJob()
    {
        var db = NewDb();
        var coll = db.GetCollection<BsonDocument>("kg.enrichments");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument
                {
                    ["jobId"] = 1,
                    ["pageId"] = 1,
                    ["fieldPath"] = 1,
                },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "jobId_1_pageId_1_fieldPath_1_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("jobId", new BsonDocument { ["$type"] = "string", ["$gt"] = "" }),
                }
            )
        );

        var jobId = ObjectId.GenerateNewId().ToString();
        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["jobId"] = jobId,
            ["pageId"] = 7149,
            ["fieldPath"] = "affiliations",
            ["operation"] = "Add",
            ["status"] = "Active",
        };

        // First insert lands.
        await coll.InsertOneAsync(doc);

        // Second insert with a fresh _id but same (jobId, pageId, fieldPath) should fail E11000.
        var dup = (BsonDocument)doc.DeepClone();
        dup["_id"] = ObjectId.GenerateNewId();

        var ex = await Assert.ThrowsExactlyAsync<MongoWriteException>(async () => await coll.InsertOneAsync(dup));
        Assert.AreEqual(ServerErrorCategory.DuplicateKey, ex.WriteError.Category, "expected E11000 duplicate-key, got " + ex.WriteError.Category);
    }

    /// <summary>
    /// The legacy-row exemption: rows without <c>jobId</c> (synchronous-path /
    /// daily-pass writes from <c>HolocronAgent.EnhanceNodeAsync</c>) MUST NOT
    /// participate in the unique constraint, otherwise scheduled passes would
    /// collide on every re-emit.
    /// </summary>
    [TestMethod]
    public async Task NodeEnrichmentPartialUniqueIndex_LegacyRowsAreExempt()
    {
        var db = NewDb();
        var coll = db.GetCollection<BsonDocument>("kg.enrichments");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument
                {
                    ["jobId"] = 1,
                    ["pageId"] = 1,
                    ["fieldPath"] = 1,
                },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "jobId_1_pageId_1_fieldPath_1_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("jobId", new BsonDocument { ["$type"] = "string", ["$gt"] = "" }),
                }
            )
        );

        // Two rows with NO jobId field — both should land. Synchronous path emits these.
        var noJob1 = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["pageId"] = 7149,
            ["fieldPath"] = "affiliations",
            ["operation"] = "Add",
        };
        var noJob2 = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["pageId"] = 7149,
            ["fieldPath"] = "affiliations",
            ["operation"] = "Add",
        };

        await coll.InsertOneAsync(noJob1);
        await coll.InsertOneAsync(noJob2);

        // Empty-string jobId should also be exempt — `$gt: ""` excludes it.
        var emptyJob = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["jobId"] = "",
            ["pageId"] = 7149,
            ["fieldPath"] = "affiliations",
            ["operation"] = "Add",
        };
        await coll.InsertOneAsync(emptyJob);

        var count = await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.AreEqual(3, count, "all three legacy rows should have landed without conflict");
    }

    /// <summary>
    /// Same shape, applied to <c>kg.edge_enrichments</c> with the
    /// <c>(jobId, fromId, toId, label)</c> composite key.
    /// </summary>
    [TestMethod]
    public async Task EdgeEnrichmentPartialUniqueIndex_RejectsDuplicateOnSameJob()
    {
        var db = NewDb();
        var coll = db.GetCollection<BsonDocument>("kg.edge_enrichments");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument
                {
                    ["jobId"] = 1,
                    ["fromId"] = 1,
                    ["toId"] = 1,
                    ["label"] = 1,
                },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "jobId_1_fromId_1_toId_1_label_1_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("jobId", new BsonDocument { ["$type"] = "string", ["$gt"] = "" }),
                }
            )
        );

        var jobId = ObjectId.GenerateNewId().ToString();
        var doc = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["jobId"] = jobId,
            ["fromId"] = 7149,
            ["toId"] = 453275,
            ["label"] = "affiliated_with",
            ["operation"] = "Annotate",
        };

        await coll.InsertOneAsync(doc);

        var dup = (BsonDocument)doc.DeepClone();
        dup["_id"] = ObjectId.GenerateNewId();

        var ex = await Assert.ThrowsExactlyAsync<MongoWriteException>(async () => await coll.InsertOneAsync(dup));
        Assert.AreEqual(ServerErrorCategory.DuplicateKey, ex.WriteError.Category);
    }

    /// <summary>
    /// Different jobIds for the same (pageId, fieldPath) MUST be allowed — that's how
    /// re-enhancement works after Phase 1 staleness flips an old enrichment to Stale
    /// and a new run produces a fresh one.
    /// </summary>
    [TestMethod]
    public async Task NodeEnrichmentPartialUniqueIndex_AllowsSameTripleAcrossJobs()
    {
        var db = NewDb();
        var coll = db.GetCollection<BsonDocument>("kg.enrichments");

        await coll.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument
                {
                    ["jobId"] = 1,
                    ["pageId"] = 1,
                    ["fieldPath"] = 1,
                },
                new CreateIndexOptions<BsonDocument>
                {
                    Name = "jobId_1_pageId_1_fieldPath_1_unique",
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("jobId", new BsonDocument { ["$type"] = "string", ["$gt"] = "" }),
                }
            )
        );

        await coll.InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["jobId"] = ObjectId.GenerateNewId().ToString(),
                ["pageId"] = 7149,
                ["fieldPath"] = "affiliations",
                ["operation"] = "Add",
            }
        );
        await coll.InsertOneAsync(
            new BsonDocument
            {
                ["_id"] = ObjectId.GenerateNewId(),
                ["jobId"] = ObjectId.GenerateNewId().ToString(),
                ["pageId"] = 7149,
                ["fieldPath"] = "affiliations",
                ["operation"] = "Add",
            }
        );

        var count = await coll.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.AreEqual(2, count, "different jobIds for the same (pageId, fieldPath) should both land");
    }
}
