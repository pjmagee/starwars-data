using System.Collections.Specialized;
using System.Text.Json;
using System.Web;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class InfoboxDownloader
{
    readonly ILogger<InfoboxDownloader> _logger;
    readonly SettingsOptions _settingsOptions;
    readonly HttpClient _httpClient;
    readonly IMongoDatabase _rawDb;

    static readonly char[] TrimValues = { '\'', '"', ',', '.', ':', '-' };

    static readonly char[] TrimContents = { '\'', '"', ',', '.' };

    static readonly Uri FandomUri = new("https://starwars.fandom.com/");
    static readonly Uri FandomWikiUri = new(FandomUri, new Uri("/wiki/", UriKind.Relative));

    bool _shouldContinue;
    int _pwpContinue = 1;

    public InfoboxDownloader(
        ILogger<InfoboxDownloader> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient,
        HttpClient httpClient
    )
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _httpClient = httpClient;
        _rawDb = mongoClient.GetDatabase(_settingsOptions.InfoboxDb);
        _pwpContinue = _settingsOptions.PageStart;
    }

    string GetPagesWithInfoboxes()
    {
        NameValueCollection queryString = HttpUtility.ParseQueryString(string.Empty);
        queryString.Add("action", "query");
        queryString.Add("list", "pageswithprop");
        queryString.Add("pwppropname", "infoboxes");
        queryString.Add("pwpprop", "ids|title|value");
        queryString.Add("pwplimit", $"{_settingsOptions.PageLimit}");
        queryString.Add("pwpcontinue", $"{_pwpContinue}");
        queryString.Add("format", "json");
        return queryString.ToString()!;
    }

    public async Task DownloadInfoboxesAsync(CancellationToken token)
    {
        do
        {
            // Get the API response
            using var response = await _httpClient.GetAsync(
                $"api.php?{GetPagesWithInfoboxes()}",
                HttpCompletionOption.ResponseHeadersRead,
                token
            );
            response.EnsureSuccessStatusCode();

            // Parse the JSON from the stream
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var pages = await JsonDocument.ParseAsync(stream, default, token);

            _shouldContinue = pages.RootElement.TryGetProperty("continue", out var continueElement);

            if (_shouldContinue)
            {
                _pwpContinue = int.Parse(continueElement.GetProperty("pwpcontinue").GetString()!);
            }

            if (_settingsOptions.FirstPageOnly)
            {
                _shouldContinue = false;
            }

            if (
                pages.RootElement.TryGetProperty("query", out var queryElement)
                && queryElement.TryGetProperty("pageswithprop", out var pagesWithPropElement)
            )
            {
                var wikiPages = pagesWithPropElement
                    .EnumerateArray()
                    .Where(x =>
                        x.GetProperty("ns").GetInt32().Equals(_settingsOptions.PageNamespace)
                    )
                    .ToList();

                var options = new ParallelOptions { CancellationToken = token };

                await Parallel.ForEachAsync(
                    wikiPages,
                    options,
                    async (wikiPage, t) =>
                    {
                        try
                        {
                            await ProcessWikiPage(wikiPage, t);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception, "error: {Error}", exception.Message);
                        }
                    }
                );
            }
        } while (_shouldContinue);
    }

    async Task ProcessWikiPage(JsonElement page, CancellationToken cancellationToken)
    {
        Infobox record = new Infobox();

        if (
            page.TryGetProperty("pageid", out var pageid) && pageid.TryGetInt32(out int pageidValue)
        )
        {
            record.PageId = pageidValue;
        }

        if (page.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            var titleValue = title.GetString()!;
            record.PageUrl = new Uri(FandomWikiUri, titleValue.Replace(" ", "_")).ToString();
        }

        if (
            page.TryGetProperty("value", out var infoboxJsonValue)
            && infoboxJsonValue.ValueKind == JsonValueKind.String
        )
        {
            using (var valueDocument = JsonDocument.Parse(infoboxJsonValue.GetString()!))
            {
                var infobox = valueDocument.RootElement.EnumerateArray().FirstOrDefault();

                if (
                    infobox.ValueKind == JsonValueKind.Object
                    && infobox.TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (
                            item.TryGetProperty("type", out var typeProperty)
                            && item.TryGetProperty("data", out var dataProperty)
                            && typeProperty.ValueKind == JsonValueKind.String
                        )
                        {
                            if (
                                typeProperty.ValueEquals("image")
                                && dataProperty.ValueKind == JsonValueKind.Array
                            )
                            {
                                var imageElement = dataProperty.EnumerateArray().FirstOrDefault();

                                if (
                                    imageElement.ValueKind == JsonValueKind.Object
                                    && imageElement.TryGetProperty("url", out var url)
                                )
                                {
                                    record.ImageUrl = url.GetString()!;
                                }
                            }
                            else if (
                                typeProperty.ValueEquals("title")
                                && dataProperty.TryGetProperty("value", out var titleDataValue)
                            )
                            {
                                record.Data.Add(
                                    new InfoboxProperty(
                                        "Titles",
                                        await GetDataValue(titleDataValue.GetString()!)
                                    )
                                );
                            }
                            else if (
                                typeProperty.ValueEquals("group")
                                && dataProperty.TryGetProperty("value", out var groupDataValue)
                                && groupDataValue.ValueKind == JsonValueKind.Array
                            )
                            {
                                var dataValues = groupDataValue
                                    .EnumerateArray()
                                    .Where(x =>
                                        x.TryGetProperty("type", out var t)
                                        && t.ValueKind == JsonValueKind.String
                                        && t.ValueEquals("data")
                                        && x.TryGetProperty("data", out var d)
                                        && d.ValueKind == JsonValueKind.Object
                                    );

                                foreach (var dataValueItem in dataValues)
                                {
                                    if (
                                        dataValueItem.TryGetProperty(
                                            "data",
                                            out var dataValueItemData
                                        )
                                        && dataValueItemData.ValueKind == JsonValueKind.Object
                                    )
                                    {
                                        if (
                                            dataValueItemData.TryGetProperty("label", out var label)
                                            && label.ValueKind == JsonValueKind.String
                                            && !label.ValueEquals(string.Empty)
                                            && dataValueItemData.TryGetProperty(
                                                "value",
                                                out var value
                                            )
                                            && value.ValueKind == JsonValueKind.String
                                        )
                                        {
                                            record.Data.Add(
                                                new InfoboxProperty(
                                                    await GetLabelValue(label.GetString()!),
                                                    await GetDataValue(value.GetString()!)
                                                )
                                            );
                                        }
                                    }
                                }
                            }
                            else if (
                                typeProperty.ValueEquals("navigation")
                                && dataProperty.TryGetProperty("value", out var navigationDataValue)
                            )
                            {
                                record.TemplateUrl = new Uri(
                                    FandomUri,
                                    await GetTemplateValue(navigationDataValue.GetString()!)
                                ).ToString();
                            }
                        }
                    }
                }
            }
        }

        // Determine continuity before saving
        record.Continuity = DetermineContinuity(record);

        await Save(record, cancellationToken);
    }

    /// <summary>
    /// Determines the continuity of an infobox based on its URL
    /// </summary>
    /// <param name="infobox">The infobox to analyze</param>
    /// <returns>The determined continuity</returns>
    Continuity DetermineContinuity(Infobox infobox)
    {
        // Simple URL-based continuity determination
        // If "/Legends" is found in the URL, it's Legends content
        // Otherwise, it's Canon content
        if (infobox.PageUrl?.Contains("/Legends", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Continuity.Legends;
        }

        return Continuity.Canon;
    }

    async Task<string> GetLabelValue(string label)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(label));
        doc.QuerySelectorAll("sup").Do(static sup => sup.Remove());
        return doc.Body?.TextContent.Trim(TrimContents) ?? string.Empty;
    }

    async Task<string?> GetTemplateValue(string html)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(html.Replace("\\", string.Empty)));

        return doc.QuerySelectorAll("a")
            .Select(static x => new HyperLink
            {
                Content = x.TextContent.Trim(TrimContents),
                Href = x.GetAttribute("href") ?? string.Empty,
            })
            .FirstOrDefault()
            ?.Href;
    }

    async Task<DataValue> GetDataValue(string html)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(html.Replace("\\", string.Empty)));
        doc.QuerySelectorAll("sup").Do(static sup => sup.Remove());
        doc.QuerySelectorAll("br").Do(static br => br.Insert(AdjacentPosition.BeforeBegin, "\n"));
        doc.QuerySelectorAll("li").Do(static li => li.Insert(AdjacentPosition.BeforeBegin, "\n"));

        HyperLink LinkSelector(IElement x)
        {
            var href = x.GetAttribute("href")!;

            if (href.StartsWith("/wiki/"))
            {
                href = new Uri(FandomUri, href).ToString();
            }

            return new HyperLink { Content = x.TextContent.Trim(TrimContents), Href = href };
        }

        return new DataValue
        {
            Values =
                doc.Body?.Text()
                    .Split('\n')
                    .Concat(doc.QuerySelectorAll("li").SelectMany(static x => x.Text().Split('\n')))
                    .Select(static x => x.Trim(TrimValues))
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .Distinct()
                    .ToList() ?? [],

            Links = doc.QuerySelectorAll("a").Select(LinkSelector).ToList(),
        };
    }

    async Task Save(Infobox record, CancellationToken token)
    {
        try
        {
            var collection = _rawDb.GetCollection<Infobox>(record.Template);
            var filter = Builders<Infobox>.Filter.Eq(x => x.PageId, record.PageId);
            var options = new ReplaceOptions { IsUpsert = true };
            await collection.ReplaceOneAsync(filter, record, options, token);

            _logger.LogInformation(
                "Saved InfoboxRecord for PageId {PageId} - {PageTitle}",
                record.PageId,
                record.PageTitle
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to save InfoboxRecord with PageId {PageId}: {ErrorMessage}",
                record.PageId,
                ex.Message
            );
        }
    }
}
