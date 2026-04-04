using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using ReverseMarkdown;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class PageDownloader
{
    readonly HttpClient _http;
    readonly ILogger<PageDownloader> _logger;
    readonly IMongoDatabase _mongoDatabase;
    readonly IMongoCollection<Page> _pagesCollection;
    readonly IMongoCollection<JobState> _jobStateCollection;
    readonly ReverseMarkdown.Converter _markdownConverter;

    readonly SettingsOptions _config;

    const string WikiBase = "https://starwars.fandom.com/wiki/";
    const string FandomBase = "https://starwars.fandom.com";
    const string PageDownloadJobName = "PageDownload";
    const string IncrementalSyncJobName = "IncrementalSync";

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
        _mongoDatabase = mongoClient.GetDatabase(settings.Value.DatabaseName);
        _pagesCollection = _mongoDatabase.GetCollection<Page>(Collections.Pages);
        _jobStateCollection = _mongoDatabase.GetCollection<JobState>(Collections.JobState);
        _markdownConverter = new ReverseMarkdown.Converter(
            new ReverseMarkdown.Config
            {
                UnknownTags = Config.UnknownTagsOption.Bypass,
                GithubFlavored = true,
                RemoveComments = true,
                SmartHrefHandling = true,
            }
        );
    }

    static string BuildQueryString(IDictionary<string, string?> parameters)
    {
        var nvc = HttpUtility.ParseQueryString(string.Empty);
        foreach (var kvp in parameters)
            if (!string.IsNullOrEmpty(kvp.Value))
                nvc[kvp.Key] = kvp.Value;

        return "?" + nvc;
    }

    /// <summary>
    /// Fetches a single wiki page. Uses 3 parallel API calls:
    /// 1) Combined metadata (pageId + infobox + categories + timestamp)
    /// 2) Rendered HTML → Markdown content
    /// 3) Image metadata
    /// </summary>
    public async Task<Page> FetchPageAsync(
        string title,
        CancellationToken cancellationToken = default
    )
    {
        var metadataTask = FetchPageMetadataAsync(title, cancellationToken);
        var contentTask = FetchContentAsMarkdownAsync(title, cancellationToken);
        var imagesTask = FetchImagesAsync(title, cancellationToken);

        await Task.WhenAll(metadataTask, contentTask, imagesTask);

        var (pageId, infobox, categories, lastModified) = metadataTask.Result;

        var page = new Page
        {
            PageId = pageId,
            Title = title,
            Infobox = infobox,
            Content = contentTask.Result,
            Categories = categories,
            Images = imagesTask.Result,
            WikiUrl = $"{WikiBase}{Uri.EscapeDataString(title.Replace(' ', '_'))}",
            LastModified = lastModified,
            Continuity = DetermineContinuity(title, categories),
            Universe = DetermineUniverse(categories),
            DownloadedAt = DateTime.UtcNow,
        };

        page.ContentHash = ComputeContentHash(page);
        return page;
    }

    /// <summary>
    /// Single API call combining pageprops (infobox), categories, and revisions (timestamp).
    /// Returns pageId, parsed infobox, category list, and last-modified date.
    /// </summary>
    async Task<(
        int pageId,
        PageInfobox? infobox,
        List<string> categories,
        DateTime lastModified
    )> FetchPageMetadataAsync(string title, CancellationToken cancellationToken)
    {
        var url = BuildQueryString(
            new Dictionary<string, string?>
            {
                ["action"] = "query",
                ["prop"] = "pageprops|categories|revisions",
                ["ppprop"] = "infoboxes",
                ["rvprop"] = "timestamp",
                ["rvlimit"] = "1",
                ["cllimit"] = _config.PageLimit.ToString(),
                ["titles"] = title,
                ["format"] = "json",
                ["formatversion"] = "2",
            }
        );

        var json = await _http.GetStringAsync(url, cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var pages = doc.RootElement.GetProperty("query").GetProperty("pages");
        var firstPage = pages.EnumerateArray().First();

        var pageId = firstPage.GetProperty("pageid").GetInt32();

        // Parse infobox from pageprops
        PageInfobox? infobox = null;
        try
        {
            if (
                firstPage.TryGetProperty("pageprops", out var pageProps)
                && pageProps.TryGetProperty("infoboxes", out var infoboxProp)
            )
            {
                var infoboxRaw = infoboxProp.GetString();
                if (!string.IsNullOrEmpty(infoboxRaw))
                {
                    using var infDoc = JsonDocument.Parse(infoboxRaw);
                    infobox = await ConvertJsonElementToPageInfobox(infDoc.RootElement);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse infobox for {Title}", title);
        }

        // Parse categories
        var categories = new List<string>();
        if (firstPage.TryGetProperty("categories", out var categoriesElement))
        {
            categories = categoriesElement
                .EnumerateArray()
                .Select(c => c.GetProperty("title").GetString())
                .Where(c => c != null)
                .Cast<string>()
                .ToList();
        }

        // Parse last modified timestamp
        var lastModified = DateTime.UtcNow;
        if (
            firstPage.TryGetProperty("revisions", out var revisions)
            && revisions.GetArrayLength() > 0
        )
        {
            var timestamp = revisions[0].GetProperty("timestamp").GetString();
            if (DateTime.TryParse(timestamp, out var parsed))
                lastModified = parsed;
        }

        return (pageId, infobox, categories, lastModified);
    }

    /// <summary>
    /// Fetches rendered HTML via action=parse, strips wiki chrome, converts to Markdown.
    /// Preserves links as absolute Fandom URLs.
    /// </summary>
    async Task<string> FetchContentAsMarkdownAsync(
        string title,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var url = BuildQueryString(
                new Dictionary<string, string?>
                {
                    ["action"] = "parse",
                    ["page"] = title,
                    ["prop"] = "text",
                    ["disableeditsection"] = "1",
                    ["format"] = "json",
                    ["formatversion"] = "2",
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("parse", out var parseElement))
                return string.Empty;

            string? html = null;
            if (parseElement.TryGetProperty("text", out var textElement))
            {
                if (
                    textElement.ValueKind == JsonValueKind.Object
                    && textElement.TryGetProperty("*", out var contentElement)
                )
                {
                    html = contentElement.GetString();
                }
                else if (textElement.ValueKind == JsonValueKind.String)
                {
                    html = textElement.GetString();
                }
            }

            if (string.IsNullOrEmpty(html))
                return string.Empty;

            // Clean wiki-specific HTML before markdown conversion
            using var context = BrowsingContext.New(Configuration.Default);
            using var document = await context.OpenAsync(
                req => req.Content(html),
                cancellationToken
            );

            // Remove elements that aren't useful article content
            foreach (
                var el in document.QuerySelectorAll(
                    ".portable-infobox, .reference, .references, .mw-references-wrap, "
                        + "#toc, .toc, .navbox, .noprint, .mw-editsection, "
                        + ".messagebox, .ambox, .mw-empty-elt, .hidden-content, "
                        + "sup.reference, .gallery, .wikia-gallery"
                )
            )
            {
                el.Remove();
            }

            // Convert relative wiki links to absolute URLs
            foreach (var link in document.QuerySelectorAll("a[href^='/wiki/']"))
            {
                var href = link.GetAttribute("href");
                link.SetAttribute("href", $"{FandomBase}{href}");
            }

            var cleanedHtml = document.Body?.InnerHtml ?? string.Empty;
            return _markdownConverter.Convert(cleanedHtml).Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch content for {Title}", title);
            return string.Empty;
        }
    }

    async Task<PageInfobox?> ConvertJsonElementToPageInfobox(JsonElement jsonElement)
    {
        var infobox = new PageInfobox();

        try
        {
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                var firstInfobox = jsonElement.EnumerateArray().FirstOrDefault();

                if (
                    firstInfobox.ValueKind == JsonValueKind.Object
                    && firstInfobox.TryGetProperty("data", out var data)
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
                                    && imageElement.TryGetProperty("url", out var imageUrl)
                                )
                                {
                                    infobox.ImageUrl = imageUrl.GetString()!;
                                }
                            }
                            else if (
                                typeProperty.ValueEquals("title")
                                && dataProperty.TryGetProperty("value", out var titleDataValue)
                            )
                            {
                                infobox.Data.Add(
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
                                            infobox.Data.Add(
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
                                var templatePath = await GetTemplateValue(
                                    navigationDataValue.GetString()!
                                );

                                if (!string.IsNullOrEmpty(templatePath))
                                {
                                    infobox.Template = new Uri(
                                        new Uri("https://starwars.fandom.com/"),
                                        templatePath
                                    ).ToString();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert JsonElement to Infobox");
        }

        return
            infobox.Data.Count > 0
            || !string.IsNullOrEmpty(infobox.ImageUrl)
            || !string.IsNullOrEmpty(infobox.Template)
            ? infobox
            : null;
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

    async Task<string?> GetTemplateValue(string html)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(html.Replace("\\", string.Empty)));

        return doc.QuerySelectorAll("a")
            .Select(static x => new HyperLink
            {
                Content = x.TextContent.Trim(' ', '"', ',', '.'),
                Href = x.GetAttribute("href") ?? string.Empty,
            })
            .FirstOrDefault()
            ?.Href;
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
                .EnumerateArray()
                .Where(p => p.TryGetProperty("imageinfo", out _))
                .Select(p =>
                {
                    var imageInfo = p.GetProperty("imageinfo")[0];
                    var imageTitle = p.GetProperty("title").GetString() ?? "Unknown";
                    var imageUrl = imageInfo.GetProperty("url").GetString() ?? string.Empty;
                    var size = imageInfo.TryGetProperty("size", out var sizeEl)
                        ? (long?)sizeEl.GetInt64()
                        : null;

                    return new MediaInfo
                    {
                        Title = imageTitle,
                        Url = imageUrl,
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

    /// <summary>
    /// Downloads a single wiki page by title and saves it to the Pages collection.
    /// </summary>
    public async Task DownloadAndSavePageAsync(
        string title,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Downloading single page: {Title}", title);
        var page = await FetchPageAsync(title, cancellationToken);
        await _pagesCollection.ReplaceOneAsync(
            Builders<Page>.Filter.Eq(p => p.PageId, page.PageId),
            page,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken
        );
        _logger.LogInformation("Saved page {PageId} ({Title}) to raw pages DB", page.PageId, title);
    }

    public async Task SyncToMongoDbAsync(CancellationToken cancellationToken)
    {
        var jobState = await _jobStateCollection
            .Find(s => s.JobName == PageDownloadJobName)
            .FirstOrDefaultAsync(cancellationToken);

        string? apContinue = jobState?.ContinueToken;

        if (apContinue != null)
            _logger.LogInformation(
                "Resuming page download from saved position: {Token}",
                apContinue
            );
        else
            _logger.LogInformation("Starting Wookieepedia page download from the beginning");

        var syncedCount = 0;
        var failedCount = 0;

        try
        {
            do
            {
                var (pageTitles, nextContinue) = await GetPageTitlesBatchAsync(
                    apContinue,
                    cancellationToken
                );

                _logger.LogInformation("Processing batch of {Count} pages", pageTitles.Count);

                var (batchSynced, batchFailed) = await ProcessPageBatchAsync(
                    pageTitles,
                    cancellationToken
                );

                syncedCount += batchSynced;
                failedCount += batchFailed;

                apContinue = nextContinue;

                // Persist progress after every batch so a cancel can resume from here
                await _jobStateCollection.ReplaceOneAsync(
                    s => s.JobName == PageDownloadJobName,
                    new JobState
                    {
                        JobName = PageDownloadJobName,
                        ContinueToken = apContinue,
                        UpdatedAt = DateTime.UtcNow,
                    },
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken
                );

                _logger.LogInformation(
                    "Synced {Synced} pages so far (failed: {Failed})",
                    syncedCount,
                    failedCount
                );
            } while (apContinue != null && !cancellationToken.IsCancellationRequested);

            if (!cancellationToken.IsCancellationRequested)
            {
                await _jobStateCollection.DeleteOneAsync(
                    s => s.JobName == PageDownloadJobName,
                    CancellationToken.None
                );
                _logger.LogInformation(
                    "Page download complete. Synced: {Synced}, Failed: {Failed}",
                    syncedCount,
                    failedCount
                );
            }
            else
            {
                _logger.LogInformation(
                    "Page download cancelled. Progress saved — next run will resume from saved position."
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during page download");
            throw;
        }
    }

    /// <summary>
    /// Incremental sync: uses the MediaWiki allrevisions API to find pages modified
    /// since the last sync, then re-downloads only those pages.
    /// </summary>
    public async Task IncrementalSyncAsync(CancellationToken cancellationToken)
    {
        var jobState = await _jobStateCollection
            .Find(s => s.JobName == IncrementalSyncJobName)
            .FirstOrDefaultAsync(cancellationToken);

        // Default to 24 hours ago if no previous sync recorded
        var since = jobState?.UpdatedAt ?? DateTime.UtcNow.AddHours(-24);
        var syncStartedAt = DateTime.UtcNow;

        _logger.LogInformation("Incremental sync: finding pages modified since {Since:u}", since);

        var changedTitles = await GetChangedPageTitlesAsync(since, cancellationToken);

        if (changedTitles.Count == 0)
        {
            _logger.LogInformation("Incremental sync: no pages changed since {Since:u}", since);
        }
        else
        {
            _logger.LogInformation(
                "Incremental sync: {Count} pages to update",
                changedTitles.Count
            );

            // Process in batches matching the normal download batch size
            var batchSize = _config.PageLimit;
            var totalSynced = 0;
            var totalFailed = 0;

            foreach (var batch in changedTitles.Chunk(batchSize))
            {
                var (synced, failed) = await ProcessPageBatchAsync(
                    batch.ToList(),
                    cancellationToken
                );
                totalSynced += synced;
                totalFailed += failed;

                _logger.LogInformation(
                    "Incremental sync progress: {Synced}/{Total} synced, {Failed} failed",
                    totalSynced,
                    changedTitles.Count,
                    totalFailed
                );

                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            _logger.LogInformation(
                "Incremental sync complete. Updated: {Synced}, Failed: {Failed}",
                totalSynced,
                totalFailed
            );
        }

        // Persist the sync timestamp so next run picks up from here
        await _jobStateCollection.ReplaceOneAsync(
            s => s.JobName == IncrementalSyncJobName,
            new JobState { JobName = IncrementalSyncJobName, UpdatedAt = syncStartedAt },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken
        );
    }

    /// <summary>
    /// Uses list=allrevisions to find all pages in namespace 0 that have been
    /// revised since a given timestamp. Returns deduplicated page titles.
    /// </summary>
    async Task<List<string>> GetChangedPageTitlesAsync(
        DateTime since,
        CancellationToken cancellationToken
    )
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? arContinue = null;

        do
        {
            var parameters = new Dictionary<string, string?>
            {
                ["action"] = "query",
                ["list"] = "allrevisions",
                ["arvprop"] = "ids|timestamp",
                ["arvnamespace"] = _config.PageNamespace.ToString(),
                ["arvstart"] = DateTime.UtcNow.ToString("o"),
                ["arvend"] = since.ToString("o"),
                ["arvlimit"] = _config.PageLimit.ToString(),
                ["format"] = "json",
                ["formatversion"] = "2",
            };

            if (arContinue != null)
                parameters["arvcontinue"] = arContinue;

            var url = BuildQueryString(parameters);
            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (
                doc.RootElement.TryGetProperty("query", out var query)
                && query.TryGetProperty("allrevisions", out var revisions)
            )
            {
                foreach (var page in revisions.EnumerateArray())
                {
                    if (page.TryGetProperty("title", out var title))
                    {
                        var titleStr = title.GetString();
                        if (titleStr != null)
                            titles.Add(titleStr);
                    }
                }
            }

            arContinue = null;
            if (
                doc.RootElement.TryGetProperty("continue", out var cont)
                && cont.TryGetProperty("arvcontinue", out var contToken)
            )
            {
                arContinue = contToken.GetString();
            }
        } while (arContinue != null && !cancellationToken.IsCancellationRequested);

        _logger.LogInformation(
            "Found {Count} distinct changed pages since {Since:u}",
            titles.Count,
            since
        );
        return titles.ToList();
    }

    async Task<(int synced, int failed)> ProcessPageBatchAsync(
        List<string> pageTitles,
        CancellationToken cancellationToken
    )
    {
        using var semaphore = new SemaphoreSlim(8, 8);
        var changedPageIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        var tasks = pageTitles.Select(async title =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var pageDoc = await FetchPageAsync(title, cancellationToken);

                // Check if content actually changed by comparing hashes
                var existingHash = await _pagesCollection
                    .Find(Builders<Page>.Filter.Eq(p => p.PageId, pageDoc.PageId))
                    .Project(p => p.ContentHash)
                    .FirstOrDefaultAsync(cancellationToken);

                var contentChanged = existingHash is null || existingHash != pageDoc.ContentHash;

                await _pagesCollection.ReplaceOneAsync(
                    Builders<Page>.Filter.Eq(p => p.PageId, pageDoc.PageId),
                    pageDoc,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken
                );

                if (contentChanged && existingHash is not null)
                    changedPageIds.Add(pageDoc.PageId);

                return 1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process page {Title}: {Error}", title, ex.Message);
                return 0;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        // Invalidate downstream pipelines for pages whose content actually changed
        if (!changedPageIds.IsEmpty)
        {
            await InvalidateDownstreamAsync(changedPageIds.ToList(), cancellationToken);
        }

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
                    ["apnamespace"] = _config.PageNamespace.ToString(),
                    ["apfilterredir"] = "nonredirects",
                    ["format"] = "json",
                    ["formatversion"] = "2",
                    ["apcontinue"] = continueToken,
                }
            );

            var json = await _http.GetStringAsync(url, cancellationToken);
            using var doc = JsonDocument.Parse(json);

            string? newContinueToken = null;
            if (doc.RootElement.TryGetProperty("continue", out var continueElement))
                newContinueToken = continueElement.GetProperty("apcontinue").GetString();

            var pageTitles = doc
                .RootElement.GetProperty("query")
                .GetProperty("allpages")
                .EnumerateArray()
                .Select(p => p.GetProperty("title").GetString())
                .Where(title => title != null)
                .Cast<string>()
                .ToList();

            return (pageTitles, newContinueToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch page titles batch: {Error}", ex.Message);
        }

        return (new List<string>(), null);
    }

    Continuity DetermineContinuity(string title, List<string> categories)
    {
        if (categories.Any(c => c.Contains("Legends articles", StringComparison.OrdinalIgnoreCase)))
        {
            return Continuity.Legends;
        }

        if (title?.Contains("/Legends", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Continuity.Legends;
        }

        if (categories.Any(c => c.Contains("Canon articles", StringComparison.OrdinalIgnoreCase)))
        {
            return Continuity.Canon;
        }

        return Continuity.Unknown;
    }

    static readonly string[] OutOfUniverseMarkers =
    [
        "Real-world articles",
        "Real-world media",
        "Out-of-universe articles",
        "Canceled projects",
        "Hoax articles",
        "Wookieepedia administration",
    ];

    Universe DetermineUniverse(List<string> categories)
    {
        if (
            categories.Any(c =>
                OutOfUniverseMarkers.Any(m => c.Contains(m, StringComparison.OrdinalIgnoreCase))
            )
        )
        {
            return Universe.OutOfUniverse;
        }

        return Universe.InUniverse;
    }

    /// <summary>
    /// SHA-256 of Content + serialised Infobox data. Stable across re-fetches
    /// when the actual wiki content hasn't changed.
    /// </summary>
    static string ComputeContentHash(Page page)
    {
        var sb = new StringBuilder();
        sb.Append(page.Content);
        if (page.Infobox is not null)
        {
            sb.Append(page.Infobox.Template);
            sb.Append(page.Infobox.ImageUrl);
            foreach (var prop in page.Infobox.Data)
            {
                sb.Append(prop.Label);
                foreach (var v in prop.Values)
                    sb.Append(v);
                foreach (var l in prop.Links)
                {
                    sb.Append(l.Content);
                    sb.Append(l.Href);
                }
            }
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// When page content changes, reset downstream pipeline state so those pages
    /// get re-processed in the next scheduled batch run.
    /// </summary>
    async Task InvalidateDownstreamAsync(List<int> changedPageIds, CancellationToken ct)
    {
        _logger.LogInformation(
            "Content changed for {Count} pages — invalidating downstream pipelines",
            changedPageIds.Count
        );

        // Reset relationship graph crawl state → pages will be re-processed by the graph builder
        var graphDb = _mongoDatabase.Client.GetDatabase(_config.DatabaseName);
        var crawlState = graphDb.GetCollection<RelationshipCrawlState>(Collections.KgCrawlState);

        var deleteResult = await crawlState.DeleteManyAsync(
            Builders<RelationshipCrawlState>.Filter.In(s => s.PageId, changedPageIds),
            ct
        );

        if (deleteResult.DeletedCount > 0)
        {
            _logger.LogInformation(
                "Reset {Count} crawl_state entries for re-processing by graph builder",
                deleteResult.DeletedCount
            );
        }

        // Delete article chunks for changed pages → will be re-chunked
        var chunksCollection = graphDb.GetCollection<MongoDB.Bson.BsonDocument>(
            Collections.SearchChunks
        );
        var chunkDeleteResult = await chunksCollection.DeleteManyAsync(
            new MongoDB.Bson.BsonDocument(
                "pageId",
                new MongoDB.Bson.BsonDocument("$in", new MongoDB.Bson.BsonArray(changedPageIds))
            ),
            ct
        );

        if (chunkDeleteResult.DeletedCount > 0)
        {
            _logger.LogInformation(
                "Deleted {Count} article chunks for re-processing",
                chunkDeleteResult.DeletedCount
            );
        }
    }
}
