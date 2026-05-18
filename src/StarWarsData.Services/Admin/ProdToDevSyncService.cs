using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Design-033: pulls raw Wookieepedia content backward from prod into dev so dev's
/// ETL/Holocron/KG code can be exercised against recently-updated real articles.
/// Copies only <c>raw.pages</c> (the sole non-derivable collection); everything else
/// is rebuilt by the existing ETL phases.
/// <para>
/// The copy is a single server-side <c>$merge</c> aggregation: <c>mongod</c> reads
/// prod and writes dev internally — no document transits this process. The merge key
/// is <c>_id</c> (= <see cref="Page.PageId"/>, which is <c>[BsonId]</c> and therefore
/// always uniquely indexed), so no extra index is required.
/// </para>
/// This is the mirror image of Design-010 (dev → prod schema promotion) and never
/// writes prod — see the hard guard in <see cref="RefreshRawPagesAsync"/>.
/// </summary>
public sealed class ProdToDevSyncService(IOptions<SettingsOptions> settings, IMongoClient mongoClient, ILogger<ProdToDevSyncService> logger)
{
    private const string ProdDb = "starwars-prod";

    /// <param name="sinceDays">
    /// Recent-slice window: copy only pages whose <c>lastModified</c> is within the last
    /// N days (the QA case). <c>null</c> ⇒ full mirror (every prod page).
    /// </param>
    /// <param name="wipe">
    /// When true, dev's <c>raw.pages</c> is emptied before the merge so upstream-deleted
    /// pages don't linger (true mirror — Design-033 Open Question 1). Default false:
    /// additive, idempotent <c>$merge</c>.
    /// </param>
    public async Task<ProdToDevSyncResult> RefreshRawPagesAsync(int? sinceDays, bool wipe, CancellationToken ct)
    {
        var targetDbName = settings.Value.DatabaseName;

        // GUARD: never write prod from this flow. First statement — throws before any
        // target handle is taken or pipeline is built.
        if (string.Equals(targetDbName, ProdDb, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prod→Dev refresh refuses to run when the target database is starwars-prod.");

        var mode = sinceDays is { } days ? $"since-{days}d" : "full";
        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var source = mongoClient.GetDatabase(ProdDb).GetCollection<Page>(Collections.Pages);

        var filter = sinceDays is { } d ? new BsonDocument("lastModified", new BsonDocument("$gte", DateTime.UtcNow.AddDays(-d))) : new BsonDocument();

        // Count the slice up front so the job result/logs report something useful
        // ($merge itself yields no documents and no counts).
        var pagesMerged = await source.CountDocumentsAsync(filter, cancellationToken: ct);

        long deletedBeforeMerge = 0;
        if (wipe)
        {
            var devPages = mongoClient.GetDatabase(targetDbName).GetCollection<Page>(Collections.Pages);
            var del = await devPages.DeleteManyAsync(FilterDefinition<Page>.Empty, ct);
            deletedBeforeMerge = del.DeletedCount;
            logger.LogWarning("Prod→Dev wipe: emptied {Deleted} docs from {Db}.{Coll} before merge (true-mirror mode)", deletedBeforeMerge, targetDbName, Collections.Pages);
        }

        // Server-side $merge: mongod reads prod and writes dev internally.
        // BSON field names are camelCase — the $match filters on lastModified.
        var pipeline = new List<BsonDocument>();
        if (sinceDays is not null)
            pipeline.Add(new BsonDocument("$match", filter));
        pipeline.Add(
            new BsonDocument(
                "$merge",
                new BsonDocument
                {
                    ["into"] = new BsonDocument { ["db"] = targetDbName, ["coll"] = Collections.Pages },
                    ["on"] = "_id", // _id == pageId ([BsonId]) — always uniquely indexed
                    ["whenMatched"] = "replace",
                    ["whenNotMatched"] = "insert",
                }
            )
        );

        logger.LogInformation(
            "Prod→Dev refresh starting: mode={Mode}, source={Src}.{Coll}, target={Dst}.{Coll}, slice={Count} pages, wipe={Wipe}",
            mode,
            ProdDb,
            Collections.Pages,
            targetDbName,
            Collections.Pages,
            pagesMerged,
            wipe
        );

        // $merge yields no documents; the cursor completes when the server-side write
        // finishes. Drain it so we await actual completion.
        using var cursor = await source.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct);
        while (await cursor.MoveNextAsync(ct))
        { /* no-op: $merge produces no output */
        }

        // raw.job_state is deliberately left untouched (Design-033 Open Question 3):
        // this copy is not a wiki sync and must not masquerade as one in the
        // Dashboard's "Recently Synced" panel.

        sw.Stop();
        var result = new ProdToDevSyncResult
        {
            Mode = mode,
            SourceDatabase = ProdDb,
            TargetDatabase = targetDbName,
            PagesMerged = pagesMerged,
            Wiped = wipe,
            DeletedBeforeMerge = deletedBeforeMerge,
            StartedUtc = startedUtc,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
        };

        logger.LogInformation(
            "Prod→Dev refresh complete: {Pages} pages merged into {Db}.{Coll} in {Elapsed:n1}s (mode={Mode}, wiped={Wiped})",
            result.PagesMerged,
            targetDbName,
            Collections.Pages,
            result.ElapsedSeconds,
            mode,
            wipe
        );

        return result;
    }
}
