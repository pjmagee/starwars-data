using System.Collections.Concurrent;
using System.Text.Json;
using AngleSharp.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Queries;
using TimelineEvent = StarWarsData.Models.Entities.TimelineEvent;

#pragma warning disable SKEXP0001
#pragma warning disable SKEXP0050
#pragma warning disable SKEXP0010

namespace StarWarsData.Services;

public class RecordService
{
    readonly ILogger<RecordService> _logger;
    readonly RecordToEventsTransformer _recordToEventsTransformer;
    readonly SettingsOptions _settingsOptions;
    readonly IMongoClient _mongoClient;
    readonly string _rawDbName;
    readonly string _structuredDbName;

    readonly YearHelper _yearHelper;

    // readonly IVectorStore _vectorStore;
    readonly ITextEmbeddingGenerationService _textEmbeddingGenerationService;

    public RecordService(
        ILogger<RecordService> logger,
        IOptions<SettingsOptions> settingsOptions,
        YearHelper yearHelper,
        IMongoClient mongoClient,
        ITextEmbeddingGenerationService textEmbeddingGenerationService,
        RecordToEventsTransformer recordToEventsTransformer)
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _mongoClient = mongoClient;
        _rawDbName = _settingsOptions.RawDb;
        _structuredDbName = _settingsOptions.StructuredDb;
        _yearHelper = yearHelper;
        // _vectorStore = vectorStore;
        _textEmbeddingGenerationService = textEmbeddingGenerationService;
        _recordToEventsTransformer = recordToEventsTransformer;
    }

    public async Task<List<string>> GetCollectionNames(CancellationToken cancellationToken)
    {
        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        List<string> results = (await (await rawDb
                .ListCollectionNamesAsync(options: null, cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken)).OrderBy(x => x).ToList();

        return results;
    }

    public async Task DeleteCollections(CancellationToken cancellationToken)
    {
        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        var collections = await GetCollectionNames(cancellationToken);
        foreach (var collection in collections)
        {
            await rawDb.DropCollectionAsync(collection, cancellationToken);
        }
    }

    public async Task<PagedResult> GetSearchResult(string query, int page = 1, int pageSize = 50, CancellationToken token = default)
    {
        var results = new ConcurrentBag<InfoboxRecord>();

        var opts = new ParallelOptions
        {
            CancellationToken = token
        };

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        var collectionNames = await (await rawDb.ListCollectionNamesAsync(cancellationToken: token)).ToListAsync(token);

        await Parallel.ForEachAsync(collectionNames, opts, async (name, t) =>
            {
                var collection = rawDb.GetCollection<InfoboxRecord>(name);

                var cursor = await collection.FindAsync(new FilterDefinitionBuilder<InfoboxRecord>().Text(query), cancellationToken: t);
                var collectionResults = await cursor.ToListAsync(t);

                foreach (var result in collectionResults)
                {
                    results.Add(result);
                }
            }
        );

        return new PagedResult
        {
            Total = results.Count,
            Size = pageSize,
            Page = page,
            Items = results.Skip((page - 1) * pageSize).Take(pageSize)
        };
    }

    public async Task<PagedResult> GetCollectionResult(string collectionName, string? searchText = null, int page = 1, int pageSize = 50, CancellationToken token = default)
    {
        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        return await GetPagerResultAsync(page, pageSize, rawDb.GetCollection<InfoboxRecord>(collectionName), searchText, token);
    }

    static async Task<PagedResult> GetPagerResultAsync(int page, int pageSize, IMongoCollection<InfoboxRecord> collection, string? searchText, CancellationToken token = default)
    {
        var total = await collection.CountDocumentsAsync(record => true, cancellationToken: token);

        var data = await collection
            .Find(searchText is null ? FilterDefinition<InfoboxRecord>.Empty : new FilterDefinitionBuilder<InfoboxRecord>().Text(searchText))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(token);

        return new PagedResult
        {
            Total = (int)total,
            Size = pageSize,
            Page = page,
            Items = data
        };
    }

    public async Task PopulateAsync(CancellationToken cancellationToken)
    {
        var insertOneOptions = new InsertOneOptions
        {
            BypassDocumentValidation = false
        };

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken
        };

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        foreach (var dataDirectory in new DirectoryInfo(_settingsOptions.DataDirectory).EnumerateDirectories())
        {
            _logger.LogInformation("Populating {Name}", dataDirectory.Name);
            await rawDb.DropCollectionAsync(dataDirectory.Name, cancellationToken);
            await rawDb.CreateCollectionAsync(dataDirectory.Name, cancellationToken: cancellationToken);

            var collection = rawDb.GetCollection<InfoboxRecord>(dataDirectory.Name);
            await collection.Indexes.DropAllAsync(cancellationToken);

            await Parallel.ForEachAsync(dataDirectory.EnumerateFiles(), parallelOptions, async (file, token) =>
                {
                    await using var jsonStream = file.OpenRead();
                    InfoboxRecord record = (await JsonSerializer.DeserializeAsync<InfoboxRecord>(jsonStream, EntitySerializerContext.Default.InfoboxRecord, cancellationToken: token))!;
                    await collection.InsertOneAsync(record, insertOneOptions, token);
                }
            );

            var indexModel = new CreateIndexModel<InfoboxRecord>(Builders<InfoboxRecord>.IndexKeys.Text("$**"));

            var index = await collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken);

            _logger.LogInformation("Index: {Index} created for {Name}", index, dataDirectory.Name);
        }

        _logger.LogInformation("Db Populated");
    }

    public async Task ProcessEmbeddingsAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<float> MaxPool(IList<ReadOnlyMemory<float>> embeddings)
        {
            int dim = embeddings[0].Length;

            // initialize with the smallest possible floats
            var maxPooled = new float[dim];

            for (int j = 0; j < dim; j++)
            {
                maxPooled[j] = float.MinValue;
            }

            // “let only the biggest values through”
            foreach (var emb in embeddings)
            {
                var span = emb.Span;

                for (int j = 0; j < dim; j++)
                {
                    // compare current max with this chunk’s value
                    if (span[j] > maxPooled[j])
                    {
                        maxPooled[j] = span[j];
                    }
                }
            }

            return maxPooled;
        }

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        foreach (var collectionName in await GetCollectionNames(cancellationToken))
        {
            // var vecCol = _vectorStore.GetCollection<string, Record>(collectionName);
            var collection = rawDb.GetCollection<InfoboxRecord>(collectionName);
            var filter = Builders<InfoboxRecord>.Filter.Exists("embedding") & Builders<InfoboxRecord>.Filter.Exists("Data");
            var count = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
            if (count == 0) continue;

            _logger.LogInformation("Processing {Count} records in {Collection}", count, collectionName);

            var cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);

            while (await cursor.MoveNextAsync(cancellationToken))
            {
                var records = cursor.Current.ToList();

                foreach (var record in records)
                {
                    var chunked = TextChunker.SplitPlainTextLines(record.EmbeddingText, maxTokensPerLine: 800);
                    IList<ReadOnlyMemory<float>> embedding = await _textEmbeddingGenerationService.GenerateEmbeddingsAsync(chunked, cancellationToken: cancellationToken);
                    record.Embedding = MaxPool(embedding);
                }

                // await vecCol.UpsertAsync(records, cancellationToken);
            }
        }
    }

    public async Task CreateTimelineEvents(CancellationToken token)
    {
        // Use TimelineEventDocument for the collection type in structured zone
        var structuredDb = _mongoClient.GetDatabase(_structuredDbName);
        var timelineEventsCollection = structuredDb.GetCollection<TimelineEvent>("Timeline_events");
        // Use TimelineEventDocument for the filter
        await timelineEventsCollection.DeleteManyAsync(FilterDefinition<TimelineEvent>.Empty, token);

        // Create indexes using TimelineEventDocument
        var indexKeysDefinition = Builders<TimelineEvent>.IndexKeys
            .Ascending(x => x.Demarcation)
            .Ascending(x => x.Year);

        var indexModel = new CreateIndexModel<TimelineEvent>(indexKeysDefinition);
        await timelineEventsCollection.Indexes.CreateOneAsync(indexModel, cancellationToken: token);

        var templateIndexKeysDefinition = Builders<TimelineEvent>.IndexKeys.Ascending(x => x.Template);
        var templateIndexModel = new CreateIndexModel<TimelineEvent>(templateIndexKeysDefinition);
        await timelineEventsCollection.Indexes.CreateOneAsync(templateIndexModel, cancellationToken: token);

        // Index on CleanedTemplate
        var cleanedTemplateIndexKeysDefinition = Builders<TimelineEvent>.IndexKeys.Ascending(x => x.CleanedTemplate);
        var cleanedTemplateIndexModel = new CreateIndexModel<TimelineEvent>(cleanedTemplateIndexKeysDefinition);
        await timelineEventsCollection.Indexes.CreateOneAsync(cleanedTemplateIndexModel, cancellationToken: token);

        _logger.LogInformation("Indexes created for Timeline_events collection");

        const int batchSize = 1000;
        // get raw zone template collections
        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        var collectionNames = await GetCollectionNames(token);
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = token
        };

        collectionNames = collectionNames.Except(["Timeline_events"]).ToList();

        // Process raw collections in parallel to generate timeline events
        await Parallel.ForEachAsync(collectionNames, parallelOptions, async (collectionName, ct) =>
            {
                _logger.LogInformation("Creating timeline events for {CollectionName}", collectionName);
                var collection = rawDb.GetCollection<InfoboxRecord>(collectionName);
                // Each parallel task needs its own batch and counter
                var batch = new List<TimelineEvent>(batchSize);
                int totalAdded = 0;

                using var cursor = await collection.Find(FilterDefinition<InfoboxRecord>.Empty).ToCursorAsync(ct);

                // Iterates through records in the current collection
                while (await cursor.MoveNextAsync(ct))
                {
                    foreach (var record in cursor.Current)
                    {
                        // Transforms the record into potential TimelineEventDocument objects
                        var events = _recordToEventsTransformer.Transform(record);
                        batch.AddRange(events); // Adds the generated events to the batch

                        // Inserts batch if full
                        if (batch.Count >= batchSize)
                        {
                            // Insert TimelineEventDocument objects
                            await timelineEventsCollection.InsertManyAsync(batch, cancellationToken: ct);
                            totalAdded += batch.Count;
                            _logger.LogInformation("[{CollectionName}] Inserted batch of {BatchCount} events. Total added so far: {TotalAdded}", collectionName, batch.Count,
                                totalAdded
                            ); // Added logging with collection name

                            batch.Clear();
                        }
                    }
                }

                // Inserts remaining events in the batch
                if (batch.Count > 0)
                {
                    // Insert TimelineEventDocument objects
                    await timelineEventsCollection.InsertManyAsync(batch, cancellationToken: ct);
                    totalAdded += batch.Count;
                    _logger.LogInformation("[{CollectionName}] Inserted final batch of {BatchCount} events. Total added: {TotalAdded}", collectionName, batch.Count, totalAdded
                    ); // Added logging with collection name

                    batch.Clear();
                }

                // Log if no events were added for a collection
                if (totalAdded == 0)
                {
                    _logger.LogWarning("No timeline events were created for {CollectionName}", collectionName);
                }
                else
                {
                    _logger.LogInformation("Finished creating and inserting {Count} timeline events for {CollectionName}", totalAdded, collectionName);
                }
            }
        );

        _logger.LogInformation("Timeline_events collection population complete");

    }

    public async Task CreateVectorIndexesAsync(CancellationToken cancellationToken)
    {
        var collectionNames = await GetCollectionNames(cancellationToken);

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        foreach (var collectionName in collectionNames.Except("Timeline_events"))
        {
            await CreateCosineVectorIndexAsync(collectionName);
        }
    }

    async Task CreateCosineVectorIndexAsync(
        string collectionName,
        string field = "embedding",
        string indexName = "vector_index",
        int dims = 3072)
    {

        // 1. Build your index definition doc:
        var vectorDef = new BsonDocument
        {
            // for a vector index, you specify a "fields" array of vector specs:
            {
                "fields", new BsonArray
                {
                    new BsonDocument
                    {
                        {
                            "type", "vector"
                        },
                        {
                            "path", "embedding"
                        },
                        {
                            "numDimensions", dims
                        },
                        {
                            "similarity", "cosine"
                        }
                    }
                }
            }
        };

        // 2. Create the CreateSearchIndexModel:
        var model = new CreateSearchIndexModel(
            name: indexName,
            // type:  SearchIndexType.VectorSearch,
            definition: vectorDef
        );

        // 3. Ask the driver to create it:
        var collection = _mongoClient.GetDatabase(_rawDbName).GetCollection<BsonDocument>(collectionName);
        await collection.SearchIndexes.CreateOneAsync(model);

        Console.WriteLine($"Created cosine vector index for '{collection.CollectionNamespace.CollectionName}.{field}'");
    }

    public async Task DeleteVectorIndexesAsync(CancellationToken cancellationToken)
    {
        async Task DeleteCosineVectorIndexAsync(string s)
        {
            var collection = _mongoClient.GetDatabase(_rawDbName).GetCollection<BsonDocument>(s);
            await collection.SearchIndexes.DropOneAsync("vs_idx", cancellationToken);
        }

        var collectionNames = await GetCollectionNames(cancellationToken);

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        foreach (var collectionName in collectionNames.Except("Timeline_events"))
        {
            await DeleteCosineVectorIndexAsync(collectionName);


        }
    }

    public async Task DeleteOpenAiEmbeddingsAsync(CancellationToken token)
    {
        var collectionNames = await GetCollectionNames(token);

        var rawDb = _mongoClient.GetDatabase(_rawDbName);
        foreach (var collectionName in collectionNames)
        {
            var collection = rawDb.GetCollection<InfoboxRecord>(collectionName);
            var update = Builders<InfoboxRecord>.Update.Unset(new StringFieldDefinition<InfoboxRecord>("embedding"));
            await collection.UpdateManyAsync(FilterDefinition<InfoboxRecord>.Empty, update, cancellationToken: token);
            _logger.LogInformation("Embeddings removed from {CollectionName}", collectionName);
        }
    }

    public async Task AddCharacterRelationshipsAsync()
    {
        var collection = _mongoClient.GetDatabase(_rawDbName).GetCollection<BsonDocument>("Character");
        var filter = Builders<BsonDocument>.Filter.Empty;

        var updatePipeline = new[]
        {
            new BsonDocument("$set", new BsonDocument("relations",
                    new BsonDocument("$reduce", new BsonDocument
                        {
                            {
                                "input", new BsonDocument("$filter", new BsonDocument
                                    {
                                        {
                                            "input", "$Data"
                                        },
                                        {
                                            "as", "d"
                                        },
                                        {
                                            "cond", new BsonDocument("$in", new BsonArray
                                            {
                                                "$$d.Label",
                                                new BsonArray
                                                {
                                                    "Parent(s)",
                                                    "Sibling(s)",
                                                    "Children",
                                                    "Partner(s)"
                                                }
                                            }
                                        )
                                        }
                                    }
                                )
                            },
                            {
                                "initialValue", new BsonArray()
                            },
                            {
                                "in", new BsonDocument("$concatArrays", new BsonArray
                                    {
                                        "$$value",
                                        new BsonDocument("$map", new BsonDocument
                                            {
                                                {
                                                    "input", "$$this.Links"
                                                },
                                                {
                                                    "as", "l"
                                                },
                                                {
                                                    "in", "$$l.Href"
                                                }
                                            }
                                        )
                                    }
                                )
                            }
                        }
                    )
                )
            )
        };

        var updateDefinition = Builders<BsonDocument>.Update.Pipeline(updatePipeline);

        // 3) supply an UpdateOptions (can be null or new UpdateOptions())
        var options = new UpdateOptions();

        // 4) call the 3-param overload
        var result = await collection.UpdateManyAsync(
            filter,
            updateDefinition,
            options,
            cancellationToken: CancellationToken.None
        );

        Console.WriteLine($"✅ Matched: {result.MatchedCount}, Modified: {result.ModifiedCount}");
    }
}

