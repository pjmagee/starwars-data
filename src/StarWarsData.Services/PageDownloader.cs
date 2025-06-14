using System.Text.Json;
using System.Web;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class PageDownloader
{
    readonly HttpClient _http;
    readonly ILogger<PageDownloader> _logger;
    readonly IBrowsingContext _browsingContext;
    readonly IMongoDatabase _mongoDatabase;
    readonly IMongoCollection<Page> _pagesCollection;

    readonly SettingsOptions _config;

    const string WikiBase = "https://starwars.fandom.com/wiki/";

    public PageDownloader(
        HttpClient httpClient,
        IOptions<SettingsOptions> settings,
        IMongoClient mongoClient,
        ILogger<PageDownloader> logger
    )
    {
        _http = httpClient;
        _logger = logger;
        _config = settings.Value;
        _mongoDatabase = mongoClient.GetDatabase(settings.Value.RawDb);
        _pagesCollection = _mongoDatabase.GetCollection<Page>("Pages");

        var angleConfig = Configuration.Default.WithDefaultLoader();
        _browsingContext = BrowsingContext.New(angleConfig);
    }

    // Helper to build query string from parameters
    static string BuildQueryString(IDictionary<string, string?> parameters)
    {
        var nvc = HttpUtility.ParseQueryString(string.Empty);
        foreach (var kvp in parameters)
            if (!string.IsNullOrEmpty(kvp.Value))
                nvc[kvp.Key] = kvp.Value;
        return "?" + nvc;
    }

    public async Task<Page> FetchPageAsync(
        string title,
        CancellationToken cancellationToken = default
    )
    {
        // First get the page ID
        var pageInfoUrl = BuildQueryString(
            new Dictionary<string, string?>
            {
                ["action"] = "query",
                ["titles"] = title,
                ["format"] = "json",
                ["formatversion"] = "2",
            }
        );
        var pageInfoJson = await _http.GetStringAsync(pageInfoUrl, cancellationToken);
        using var pageInfoDoc = JsonDocument.Parse(pageInfoJson);

        var pages = pageInfoDoc.RootElement.GetProperty("query").GetProperty("pages");
        var firstPage = pages.EnumerateArray().First();
        var pageId = firstPage.GetProperty("pageid").GetInt32();

        return await BuildPageDocumentAsync(pageId, title, cancellationToken);
    }

    async Task<Page> BuildPageDocumentAsync(
        int pageId,
        string title,
        CancellationToken cancellationToken
    )
    {
        var infoboxTask = FetchInfoboxAsync(title, cancellationToken);
        var categoriesTask = FetchCategoriesAsync(title, cancellationToken);
        var sectionsTask = FetchSectionsWithAngleSharpAsync(title, cancellationToken);
        var imagesTask = FetchImagesAsync(title, cancellationToken);
        var pageInfoTask = FetchPageInfoAsync(title, cancellationToken);

        await Task.WhenAll(infoboxTask, categoriesTask, sectionsTask, imagesTask, pageInfoTask);

        var pageInfo = await pageInfoTask;
        var infobox = await infoboxTask;
        var categories = await categoriesTask;

        var page = new Page
        {
            PageId = pageId,
            Title = title,
            Infobox = infobox,
            Categories = categories,
            Sections = await sectionsTask,
            Images = await imagesTask,
            WikiUrl = $"{WikiBase}{Uri.EscapeDataString(title.Replace(' ', '_'))}",
            LastModified = pageInfo.lastModified,
            Summary = pageInfo.summary,
            Continuity = DetermineContinuity(infobox, categories, title),
        };

        return page;
    }

    async Task<List<InfoboxProperty>> FetchInfoboxAsync(
        string title,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "query",
                    ["prop"] = "pageprops",
                    ["ppprop"] = "infoboxes",
                    ["titles"] = title,
                    ["format"] = "json",
                    ["formatversion"] = "2",
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            var firstPage = pages.EnumerateArray().First();

            if (
                firstPage.TryGetProperty("pageprops", out var pageProps)
                && pageProps.TryGetProperty("infoboxes", out var infoboxProp)
            )
            {
                var infoboxRaw = infoboxProp.GetString();
                if (!string.IsNullOrEmpty(infoboxRaw))
                {
                    using var infDoc = JsonDocument.Parse(infoboxRaw);
                    return await ConvertJsonElementToInfoboxProperties(infDoc.RootElement);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch infobox for {Title}", title);
        }

        return new List<InfoboxProperty>();
    }

    async Task<List<InfoboxProperty>> ConvertJsonElementToInfoboxProperties(JsonElement jsonElement)
    {
        var properties = new List<InfoboxProperty>();

        try
        {
            // The infobox data from the API comes as an array of infobox objects
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                var infobox = jsonElement.EnumerateArray().FirstOrDefault();

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
                                typeProperty.ValueEquals("title")
                                && dataProperty.TryGetProperty("value", out var titleDataValue)
                            )
                            {
                                properties.Add(
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
                                            properties.Add(
                                                new InfoboxProperty(
                                                    await GetLabelValue(label.GetString()!),
                                                    await GetDataValue(value.GetString()!)
                                                )
                                            );
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert JsonElement to InfoboxProperties");
        }

        return properties;
    }

    async Task<string> GetLabelValue(string label)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(label));
        doc.QuerySelectorAll("sup").Do(static sup => sup.Remove());
        return doc.Body?.TextContent.Trim(' ', '"', ',', '.') ?? string.Empty;
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
                href = new Uri(new Uri("https://starwars.fandom.com/"), href).ToString();
            }

            return new HyperLink { Content = x.TextContent.Trim(' ', '"', ',', '.'), Href = href };
        }

        return new DataValue
        {
            Values =
                doc.Body?.Text()
                    .Split('\n')
                    .Concat(doc.QuerySelectorAll("li").SelectMany(static x => x.Text().Split('\n')))
                    .Select(static x => x.Trim(' ', '\'', '"', ',', '.', ':', '-'))
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .Distinct()
                    .ToList() ?? new List<string>(),

            Links = doc.QuerySelectorAll("a").Select(LinkSelector).ToList(),
        };
    }

    async Task<List<string>> FetchCategoriesAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "query",
                    ["prop"] = "categories",
                    ["titles"] = title,
                    ["format"] = "json",
                    ["formatversion"] = "2",
                    ["cllimit"] = _config.PageLimit.ToString(),
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            var firstPage = pages.EnumerateArray().First();

            if (firstPage.TryGetProperty("categories", out var categoriesElement))
            {
                return categoriesElement
                    .EnumerateArray()
                    .Select(c => c.GetProperty("title").GetString())
                    .Where(c => c != null)
                    .Cast<string>()
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch categories for {Title}", title);
        }

        return new List<string>();
    }

    async Task<List<ArticleSection>> FetchSectionsWithAngleSharpAsync(
        string title,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Get the full page HTML content
            var parseUrl = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "parse",
                    ["page"] = title,
                    ["format"] = "json",
                    ["formatversion"] = "2",
                    ["prop"] = "text|sections",
                }
            );
            var parseJson = await _http.GetStringAsync(parseUrl, cancellationToken);
            using var parseDoc = JsonDocument.Parse(parseJson);

            if (!parseDoc.RootElement.TryGetProperty("parse", out var parseElement))
                return new List<ArticleSection>();

            var sections = new List<ArticleSection>();

            // Get section metadata
            var sectionElements = parseElement.GetProperty("sections").EnumerateArray();
            var htmlContent = parseElement.GetProperty("text").GetProperty("*").GetString();

            if (string.IsNullOrEmpty(htmlContent))
                return sections;

            // Parse HTML with AngleSharp
            using var document = await _browsingContext.OpenAsync(
                req => req.Content(htmlContent),
                cancellationToken
            );

            // Extract lead section (before first heading)
            var leadContent = ExtractLeadSection(document);
            if (!string.IsNullOrEmpty(leadContent))
            {
                sections.Add(
                    new ArticleSection
                    {
                        Heading = "Lead",
                        Content = leadContent,
                        PlainText = StripHtmlAndClean(leadContent),
                        Level = 0,
                        Links = ExtractLinks(leadContent),
                    }
                );
            }

            // Process each section
            foreach (var sectionEl in sectionElements)
            {
                var sectionIndex = sectionEl.GetProperty("index").GetInt32();
                var sectionHeading = sectionEl.GetProperty("line").GetString() ?? "Unknown";
                var sectionLevel = sectionEl.GetProperty("level").GetInt32();

                // Get section content
                var sectionContent = await FetchSectionContentAsync(
                    title,
                    sectionIndex,
                    cancellationToken
                );
                if (!string.IsNullOrEmpty(sectionContent))
                {
                    sections.Add(
                        new ArticleSection
                        {
                            Heading = sectionHeading,
                            Content = sectionContent,
                            PlainText = StripHtmlAndClean(sectionContent),
                            Level = sectionLevel,
                            Links = ExtractLinks(sectionContent),
                        }
                    );
                }
            }

            return sections;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch sections for {Title}", title);
            return new List<ArticleSection>();
        }
    }

    string ExtractLeadSection(IDocument document)
    {
        var leadElements = new List<string>();
        var walker = document.Body?.FirstChild;

        while (walker != null)
        {
            if (walker is IHtmlHeadingElement)
                break;

            if (walker is IElement element && element.TagName != "DIV")
            {
                leadElements.Add(element.OuterHtml);
            }

            walker = walker.NextSibling;
        }

        return string.Join("\n", leadElements);
    }

    string StripHtmlAndClean(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        try
        {
            using var doc = _browsingContext.OpenAsync(req => req.Content(html)).Result;
            var text = doc.Body?.TextContent ?? string.Empty;

            // Clean up common wiki artifacts
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[edit\]", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            text = text.Trim();

            return text;
        }
        catch
        {
            return html;
        }
    }

    List<string> ExtractLinks(string html)
    {
        var links = new List<string>();
        if (string.IsNullOrEmpty(html))
            return links;

        try
        {
            using var doc = _browsingContext.OpenAsync(req => req.Content(html)).Result;
            var anchors = doc.QuerySelectorAll("a[href]");

            foreach (var anchor in anchors)
            {
                var href = anchor.GetAttribute("href");
                if (!string.IsNullOrEmpty(href) && href.Contains("/wiki/"))
                {
                    var pageName = href.Split("/wiki/").LastOrDefault();
                    if (!string.IsNullOrEmpty(pageName))
                    {
                        links.Add(Uri.UnescapeDataString(pageName.Replace('_', ' ')));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract links from HTML");
        }

        return links.Distinct().ToList();
    }

    async Task<string> FetchSectionContentAsync(
        string title,
        int sectionIndex,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "parse",
                    ["prop"] = "text",
                    ["page"] = title,
                    ["section"] = sectionIndex.ToString(),
                    ["disableeditsection"] = "1",
                    ["format"] = "json",
                    ["formatversion"] = "2",
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("parse")
                    .GetProperty("text")
                    .GetProperty("*")
                    .GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to fetch section {Index} for {Title}",
                sectionIndex,
                title
            );
            return string.Empty;
        }
    }

    async Task<List<MediaInfo>> FetchImagesAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "query",
                    ["generator"] = "images",
                    ["titles"] = title,
                    ["prop"] = "imageinfo",
                    ["iiprop"] = "url|size|mediatype",
                    ["gimlimit"] = _config.PageLimit.ToString(),
                    ["format"] = "json",
                    ["formatversion"] = "2",
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.GetProperty("query").TryGetProperty("pages", out var pages))
                return new List<MediaInfo>();

            return pages
                .EnumerateObject()
                .Where(p => p.Value.TryGetProperty("imageinfo", out _))
                .Select(p =>
                {
                    var imageInfo = p.Value.GetProperty("imageinfo")[0];
                    var title = p.Value.GetProperty("title").GetString() ?? "Unknown";
                    var url = imageInfo.GetProperty("url").GetString() ?? string.Empty;
                    var size = imageInfo.TryGetProperty("size", out var sizeEl)
                        ? (long?)sizeEl.GetInt64()
                        : null;

                    return new MediaInfo
                    {
                        Title = title,
                        Url = url,
                        Size = size,
                    };
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch images for {Title}", title);
            return new List<MediaInfo>();
        }
    }

    async Task<(DateTime lastModified, string? summary)> FetchPageInfoAsync(
        string title,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "query",
                    ["prop"] = "revisions",
                    ["rvprop"] = "timestamp|comment",
                    ["titles"] = title,
                    ["rvlimit"] = "1",
                    ["format"] = "json",
                    ["formatversion"] = "2",
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
            var firstPage = pages.EnumerateArray().First();

            if (
                firstPage.TryGetProperty("revisions", out var revisions)
                && revisions.GetArrayLength() > 0
            )
            {
                var latestRevision = revisions[0];
                var timestamp = latestRevision.GetProperty("timestamp").GetString();
                var comment = latestRevision.TryGetProperty("comment", out var commentEl)
                    ? commentEl.GetString()
                    : null;

                if (DateTime.TryParse(timestamp, out var lastModified))
                {
                    return (lastModified, comment);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch page info for {Title}", title);
        }
        return (DateTime.UtcNow, null);
    }

    public async Task SyncToMongoDbAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Wookieepedia sync to MongoDB with integrated pagination");

        string? apContinue = null;
        var syncedCount = 0;
        var failedCount = 0;

        try
        {
            do
            {
                // Get a batch of page titles from the API
                var (pageTitles, nextContinue) = await GetPageTitlesBatchAsync(
                    apContinue,
                    cancellationToken
                );
                _logger.LogInformation("Processing batch of {Count} pages", pageTitles.Count);

                // Process this batch with limited parallelism to avoid overwhelming the API
                var (batchSynced, batchFailed) = await ProcessPageBatchAsync(
                    pageTitles,
                    cancellationToken
                );
                syncedCount += batchSynced;
                failedCount += batchFailed;

                if (syncedCount % 100 == 0)
                {
                    _logger.LogInformation("Synced {Count} pages so far...", syncedCount);
                }

                apContinue = nextContinue;
            } while (apContinue != null && !cancellationToken.IsCancellationRequested);

            _logger.LogInformation(
                "Wookieepedia sync completed. Synced: {Synced}, Failed: {Failed}",
                syncedCount,
                failedCount
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Wookieepedia sync: {Error}", ex.Message);
            throw;
        }
    }

    async Task<(int synced, int failed)> ProcessPageBatchAsync(
        List<string> pageTitles,
        CancellationToken cancellationToken
    )
    {
        // Use semaphore to limit concurrent requests (avoid overwhelming the API)
        using var semaphore = new SemaphoreSlim(5, 5); // Max 5 concurrent requests

        var tasks = pageTitles.Select(async title =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var pageDoc = await FetchPageAsync(title, cancellationToken);
                if (pageDoc != null)
                {
                    // Upsert the page document into MongoDB
                    await _pagesCollection.ReplaceOneAsync(
                        Builders<Page>.Filter.Eq(p => p.PageId, pageDoc.PageId),
                        pageDoc,
                        new ReplaceOptions { IsUpsert = true },
                        cancellationToken
                    );

                    return 1; // Synced
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process page {Title}: {Error}", title, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }

            return 0; // Failed
        });

        var results = await Task.WhenAll(tasks);
        return (results.Sum(r => r), results.Length - results.Sum(r => r));
    }

    async Task<(List<string> pageTitles, string? continueToken)> GetPageTitlesBatchAsync(
        string? continueToken,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "query",
                    ["list"] = "allpages",
                    ["aplimit"] = _config.PageLimit.ToString(),
                    ["format"] = "json",
                    ["formatversion"] = "2",
                    ["apcontinue"] = continueToken,
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("continue", out var continueElement))
            {
                var newContinueToken = continueElement.GetProperty("apcontinue").GetString();
                var pageTitles = doc
                    .RootElement.GetProperty("query")
                    .GetProperty("allpages")
                    .EnumerateArray()
                    .Select(p => p.GetProperty("title").GetString())
                    .ToList();

                return (pageTitles, newContinueToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch page titles batch: {Error}", ex.Message);
        }

        return (new List<string>(), null);
    }

    /// <summary>
    /// Determines the continuity of a page based on its title
    /// </summary>
    /// <param name="infobox">The page's infobox properties (unused)</param>
    /// <param name="categories">The page's categories (unused)</param>
    /// <param name="title">The page title</param>
    /// <returns>The determined continuity</returns>
    Continuity DetermineContinuity(
        List<InfoboxProperty> infobox,
        List<string> categories,
        string title
    )
    {
        // Simple URL-based continuity determination
        // If "/Legends" is found in the title, it's Legends content
        // Otherwise, it's Canon content
        if (title?.Contains("/Legends", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Continuity.Legends;
        }

        return Continuity.Canon;
    }
}
