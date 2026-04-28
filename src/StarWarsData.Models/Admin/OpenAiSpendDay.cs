using MongoDB.Bson.Serialization.Attributes;

namespace StarWarsData.Models.Entities;

/// <summary>
/// One day of OpenAI spend, populated by the Admin app's nightly sync against
/// <c>/v1/organization/costs?bucket_width=1d&amp;group_by[]=line_item</c>.
/// Amounts stored in cents (integer) to avoid float drift; the OpenAI Costs API
/// reports figures as estimates with up to ~24h lag, so the page surfaces this
/// as &ldquo;approx&rdquo; and shows <see cref="SyncedAt"/>.
/// </summary>
public class OpenAiSpendDay
{
    /// <summary>ISO-8601 UTC date (e.g. "2026-04-27") — the bucket's calendar day.</summary>
    [BsonId]
    public string Date { get; set; } = string.Empty;

    [BsonElement("bucketStart")]
    public DateTime BucketStart { get; set; }

    [BsonElement("bucketEnd")]
    public DateTime BucketEnd { get; set; }

    /// <summary>Sum of all <see cref="LineItems"/> amounts, in cents.</summary>
    [BsonElement("totalCents")]
    public long TotalCents { get; set; }

    /// <summary>ISO 4217 lower-case (e.g. "usd"). All buckets in a sync share one currency.</summary>
    [BsonElement("currency")]
    public string Currency { get; set; } = "usd";

    /// <summary>Per-line-item breakdown as returned by the API (e.g. "gpt-4-2024-08-06, input").</summary>
    [BsonElement("lineItems")]
    public List<OpenAiSpendLineItem> LineItems { get; set; } = [];

    [BsonElement("syncedAt")]
    public DateTime SyncedAt { get; set; } = DateTime.UtcNow;
}

public class OpenAiSpendLineItem
{
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("cents")]
    public long Cents { get; set; }
}
