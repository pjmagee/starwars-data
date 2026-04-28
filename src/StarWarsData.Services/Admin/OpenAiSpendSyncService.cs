using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

/// <summary>
/// Nightly Hangfire job that pulls the last 90 days of OpenAI organisation spend
/// from <c>GET https://api.openai.com/v1/organization/costs</c> and upserts one
/// document per UTC day into <see cref="Collections.SpendDaily"/>. The public
/// <c>/api/costs/openai-summary</c> endpoint serves from this collection — runtime
/// reads never hit OpenAI directly.
///
/// Requires <see cref="SettingsOptions.OpenAiAdminKey"/> to be set; if missing,
/// the job exits silently so dev environments without a billing-scope admin key
/// don't error every night.
/// </summary>
public class OpenAiSpendSyncService
{
    readonly HttpClient _http;
    readonly IMongoCollection<OpenAiSpendDay> _spend;
    readonly SettingsOptions _settings;
    readonly ILogger<OpenAiSpendSyncService> _logger;

    public OpenAiSpendSyncService(HttpClient http, IMongoClient mongoClient, IOptions<SettingsOptions> settings, ILogger<OpenAiSpendSyncService> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
        var db = mongoClient.GetDatabase(_settings.DatabaseName);
        _spend = db.GetCollection<OpenAiSpendDay>(Collections.SpendDaily);
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.OpenAiAdminKey))
        {
            _logger.LogInformation("OpenAI spend sync skipped: OpenAiAdminKey not configured");
            return;
        }

        // Pull a 90-day window — keeps the sync idempotent (each run rewrites the
        // last 3 months). OpenAI lags ~24h so today's bucket is usually empty;
        // we still write it as 0 so the UI's day index doesn't gap.
        var endUtc = DateTime.UtcNow.Date.AddDays(1);
        var startUtc = endUtc.AddDays(-90);
        var startEpoch = new DateTimeOffset(startUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        var endEpoch = new DateTimeOffset(endUtc, TimeSpan.Zero).ToUnixTimeSeconds();

        var buckets = new List<CostsBucket>();
        string? nextPage = null;
        var pageCount = 0;

        do
        {
            var url = $"https://api.openai.com/v1/organization/costs?start_time={startEpoch}&end_time={endEpoch}&bucket_width=1d&group_by[]=line_item&limit=180";
            if (nextPage is not null)
                url += $"&page={Uri.EscapeDataString(nextPage)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenAiAdminKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OpenAI costs API returned {Status}: {Body}", resp.StatusCode, body);
                return;
            }

            var page = await resp.Content.ReadFromJsonAsync<CostsPage>(JsonOpts, ct);
            if (page is null)
                break;

            buckets.AddRange(page.Data);
            nextPage = page.HasMore ? page.NextPage : null;
            pageCount++;
        } while (nextPage is not null && pageCount < 20);

        var now = DateTime.UtcNow;
        var writes = new List<WriteModel<OpenAiSpendDay>>();

        foreach (var bucket in buckets)
        {
            var bucketStart = DateTimeOffset.FromUnixTimeSeconds(bucket.StartTime).UtcDateTime;
            var bucketEnd = DateTimeOffset.FromUnixTimeSeconds(bucket.EndTime).UtcDateTime;
            var date = bucketStart.ToString("yyyy-MM-dd");

            var lineItems = new List<OpenAiSpendLineItem>();
            string currency = "usd";
            long totalCents = 0;

            foreach (var result in bucket.Results)
            {
                if (result.Amount is null)
                    continue;
                if (!string.IsNullOrEmpty(result.Amount.Currency))
                    currency = result.Amount.Currency;
                var cents = (long)Math.Round(result.Amount.Value * 100m, MidpointRounding.AwayFromZero);
                totalCents += cents;
                lineItems.Add(new OpenAiSpendLineItem { Name = result.LineItem ?? "uncategorised", Cents = cents });
            }

            var doc = new OpenAiSpendDay
            {
                Date = date,
                BucketStart = bucketStart,
                BucketEnd = bucketEnd,
                TotalCents = totalCents,
                Currency = currency,
                LineItems = lineItems,
                SyncedAt = now,
            };

            writes.Add(new ReplaceOneModel<OpenAiSpendDay>(Builders<OpenAiSpendDay>.Filter.Eq(d => d.Date, date), doc) { IsUpsert = true });
        }

        if (writes.Count > 0)
            await _spend.BulkWriteAsync(writes, cancellationToken: ct);

        _logger.LogInformation("OpenAI spend sync complete: {Buckets} day buckets upserted", writes.Count);
    }

    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    sealed class CostsPage
    {
        [JsonPropertyName("data")]
        public List<CostsBucket> Data { get; set; } = [];

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }

        [JsonPropertyName("next_page")]
        public string? NextPage { get; set; }
    }

    sealed class CostsBucket
    {
        [JsonPropertyName("start_time")]
        public long StartTime { get; set; }

        [JsonPropertyName("end_time")]
        public long EndTime { get; set; }

        [JsonPropertyName("results")]
        public List<CostsResult> Results { get; set; } = [];
    }

    sealed class CostsResult
    {
        [JsonPropertyName("amount")]
        public CostsAmount? Amount { get; set; }

        [JsonPropertyName("line_item")]
        public string? LineItem { get; set; }
    }

    sealed class CostsAmount
    {
        [JsonPropertyName("value")]
        public decimal Value { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "usd";
    }
}
