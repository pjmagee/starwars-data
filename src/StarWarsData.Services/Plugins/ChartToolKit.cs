using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using MongoDB.Bson;
using MongoDB.Driver;
using StarWarsData.Models;

namespace StarWarsData.Services.Plugins;

#pragma warning disable SKEXP0001

public class ChartToolkit
{
    // [KernelFunction(name: "get_collections")]
    // public async Task<List<string>> GetCollectionNames()
    // {
    //     IAsyncCursor<string>? cursor = await db.ListCollectionNamesAsync();
    //     var collectionNames = await cursor.ToListAsync();
    //     return collectionNames.Except(["Timeline_events"]).ToList();
    // }
    //
    // [KernelFunction("query")]
    // public async Task<List<RowDto>> QueryAsync(
    //     string collection, // planets, powers, battles …
    //     string filterLabel, // ex: "Side", "Type"
    //     string filterValue = "", // optional; "" means “match any value”
    //     AggOp op = AggOp.Count,
    //     string? semantic = null)
    // {
    //     const int k = 100;
    //     const int nc = 500;
    //
    //     // accumulator: $sum:1 for count, otherwise sum/avg/min/max on 1
    //     var accDoc = op switch
    //     {
    //         AggOp.Count => new BsonDocument("$sum", 1),
    //         AggOp.Sum => new BsonDocument("$sum", 1),
    //         AggOp.Avg => new BsonDocument("$avg", 1),
    //         AggOp.Min => new BsonDocument("$min", 1),
    //         AggOp.Max => new BsonDocument("$max", 1),
    //         _ => new BsonDocument("$sum", 1)
    //     };
    //
    //     var pipeline = new List<BsonDocument>();
    //
    //     // 1️⃣  optional ANN semantic filter
    //     if (!string.IsNullOrWhiteSpace(semantic))
    //     {
    //         var vec = await embeddingService.GenerateEmbeddingAsync(semantic);
    //
    //         pipeline.Add(new("$vectorSearch", new BsonDocument
    //                 {
    //                     {
    //                         "index", "vs_idx"
    //                     },
    //                     {
    //                         "path", "embedding"
    //                     },
    //                     {
    //                         "queryVector", new BsonArray(vec.ToArray())
    //                     },
    //                     {
    //                         "numCandidates", nc
    //                     },
    //                     {
    //                         "limit", k
    //                     }
    //                 }
    //             )
    //         );
    //     }
    //
    //     // 2️⃣  explode Data rows
    //     pipeline.Add(new("$unwind", "$Data"));
    //
    //     // 3️⃣  keep only rows matching label/value
    //     var match = new BsonDocument("Data.Label",
    //         new BsonDocument("$regex", $"^{Regex.Escape(filterLabel)}$")
    //             .Add("$options", "i")
    //     );
    //
    //     if (!string.IsNullOrEmpty(filterValue))
    //         match.Add("Data.Values", filterValue);
    //
    //     pipeline.Add(new("$match", match));
    //
    //     // 4️⃣  add the first Titles value as Display
    //     pipeline.Add(new("$addFields", new BsonDocument("Display",
    //                 new BsonDocument("$arrayElemAt", new BsonArray
    //                     {
    //                         "$Data.Values",
    //                         0
    //                     }
    //                 )
    //             )
    //         )
    //     );
    //
    //     // 5️⃣  group by display title
    //     pipeline.Add(new("$group", new BsonDocument
    //             {
    //                 {
    //                     "_id", "$Display"
    //                 },
    //                 {
    //                     "value", accDoc
    //                 }
    //             }
    //         )
    //     );
    //
    //     var docs = await db.GetCollection<BsonDocument>(collection)
    //         .Aggregate<BsonDocument>(pipeline)
    //         .ToListAsync();
    //
    //     return docs.Select(d => new RowDto(
    //                 d["_id"].AsString,
    //                 d["value"].ToDouble()
    //             )
    //         )
    //         .ToList();
    // }
    //
    // [KernelFunction(name: "get_labels")]
    // [Description("Get the unique labels for the provided collection which is used for the query")]
    // public async Task<List<string>> GetLabels(string collection)
    // {
    //     // 1. Unwind the Data array
    //     var unwind = new BsonDocument("$unwind", "$Data");
    //     // 2. Group by Data.Label
    //     var group = new BsonDocument("$group", new BsonDocument
    //         {
    //             {
    //                 "_id", "$Data.Label"
    //             }
    //         }
    //     );
    //
    //     // 3. Project the _id back out to a string list
    //     var project = new BsonDocument("$project", new BsonDocument
    //         {
    //             {
    //                 "Label", "$_id"
    //             },
    //             {
    //                 "_id", 0
    //             }
    //         }
    //     );
    //
    //     var pipeline = new[]
    //     {
    //         unwind,
    //         group,
    //         project
    //     };
    //
    //     var result = await db
    //         .GetCollection<BsonDocument>(collection)
    //         .Aggregate<BsonDocument>(pipeline)
    //         .ToListAsync();
    //
    //     return result
    //         .Select(d => d["Label"].AsString)
    //         .ToList();
    // }

    [KernelFunction(name: "build_chart")]
    [Description("Builds a chart using the provided labels and values")]
    public ChartSpec BuildChart(
        [Description("The type of chart to build")]
        ChartKind chartKind,
        [Description("The labels")]
        string[] labels,
        [Description("The values")]
        double[] series,
        [Description("The title of the chart")]
        string title, 
        [Description("The legend of the chart")]
        string legend)
    {
        var spec = new ChartSpec
        {
            Kind = chartKind,
            Labels = labels,
            Series = series,
            Title = title,
            Legend = legend
        };
        
        return spec;
    }
}