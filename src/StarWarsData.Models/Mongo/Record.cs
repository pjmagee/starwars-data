using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.VectorData;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace StarWarsData.Models.Mongo;

[Serializable]
public class Record
{
    [JsonIgnore]
    [BsonIgnore]
    public string PageTitle => HttpUtility.UrlDecode(PageUrl!.Split(["/wiki/"], StringSplitOptions.None).Last()).Replace("_", " ");
    
    [JsonIgnore]
    [BsonIgnore] 
    public string Template => TemplateUrl.Split(':').Last().Replace("_infobox", string.Empty);
    
    [JsonInclude]
    [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
    [BsonElement("_id")]
    [VectorStoreRecordKey]
    public int PageId { get; set; }
    
    [JsonInclude]
    [BsonElement] 
    public string PageUrl { get; set; } = null!;
    
    [JsonInclude]
    [BsonElement] 
    public string TemplateUrl { get; set; } = null!;
    
    [JsonInclude]
    [BsonElement] 
    public string? ImageUrl { get; set; }
    
    [JsonInclude]
    [BsonElement]
    [VectorStoreRecordData] 
    public List<InfoboxProperty> Data { get; set; } = new();
    
    [JsonInclude]
    [BsonElement]
    [VectorStoreRecordData] 
    public List<Relationship> Relationships { get; set; } = new();
    
    [JsonIgnore]
    [BsonIgnore]
    public bool ShowRelationships { get; set; } = false;
    
    [JsonIgnore]
    [BsonElement("embedding")]
    [VectorStoreRecordVector(Dimensions: 4, DistanceFunction = DistanceFunction.CosineSimilarity, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float> Embedding { get; set; }
    
    /// <summary>
    /// Canonical text that we’ll send to the embedding model.
    /// • Uses InfoboxProperty.Label / Values / Links.Content  
    /// • Uses Relationship.PageTitle and Template (no raw URLs)  
    /// • Stays far below the 8 191-token hard limit
    /// </summary>
    [JsonIgnore, BsonIgnore]
    public string EmbeddingText
    {
        get
        {
            var sb = new StringBuilder(1024);

            // 1️⃣ Headline
            sb.AppendLine($"TITLE: {PageTitle}");
            sb.AppendLine($"TEMPLATE: {Template}");

            // 2️⃣ Infobox rows
            foreach (var prop in Data.Where(p => !string.IsNullOrWhiteSpace(p.Label)))
            {
                var vals   = prop.Values.Where(v => !string.IsNullOrWhiteSpace(v));
                var anchor = prop.Links.Where(l => !string.IsNullOrWhiteSpace(l.Content)).Select(l => l.Content)                 ?? [];
                var joined = string.Join(", ", vals.Concat(anchor));

                if (joined.Length > 0)
                {
                    sb.AppendLine($"{prop.Label!.ToUpperInvariant()}: {joined}");
                }
            }

            // 3️⃣ Linked pages (your Relationship objects)
            foreach (var rel in Relationships)
            {
                // Example: RELATED_PAGE: Coruscant (CITY)
                sb.AppendLine($"RELATED_PAGE: {rel.PageTitle}");
                sb.AppendLine($"RELATED_TEMPLATE: {rel.Template}");
            }

            return sb.ToString().TrimEnd();
        }
    }
}