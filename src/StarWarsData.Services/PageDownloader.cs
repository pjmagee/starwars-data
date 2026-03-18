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
    readonly IMongoCollection<JobState> _jobStateCollection;

    readonly SettingsOptions _config;

    const string WikiBase = "https://starwars.fandom.com/wiki/";
    const string PageDownloadJobName = "PageDownload";

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
        _mongoDatabase = mongoClient.GetDatabase(settings.Value.PagesDb);
        _pagesCollection = _mongoDatabase.GetCollection<Page>("Pages");
        _jobStateCollection = _mongoDatabase.GetCollection<JobState>("JobState");

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

        var infobox = infoboxTask.Result;
        var categories = categoriesTask.Result;
        var sections = sectionsTask.Result;
        var images = imagesTask.Result;

        var page = new Page
        {
            PageId = pageId,
            Title = title,
            Infobox = infobox,
            Categories = categories,
            Sections = sections,
            Images = images,
            WikiUrl = $"{WikiBase}{Uri.EscapeDataString(title.Replace(' ', '_'))}",
            LastModified = pageInfoTask.Result,
            Continuity = DetermineContinuity(title, categories),
            Universe = DetermineUniverse(categories),
            DownloadedAt = DateTime.UtcNow,
        };

        return page;
    }

    async Task<PageInfobox?> FetchInfoboxAsync(string title, CancellationToken cancellationToken)
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
                    return await ConvertJsonElementToPageInfobox(infDoc.RootElement);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch infobox for {Title}", title);
        }

        return null;
    }

    async Task<PageInfobox?> ConvertJsonElementToPageInfobox(JsonElement jsonElement)
    {
        var infobox = new PageInfobox();

        try
        {
            // The infobox data from the API comes as an array of infobox objects
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
                                    && imageElement.TryGetProperty("url", out var url)
                                )
                                {
                                    infobox.ImageUrl = url.GetString()!;
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
                                && dataProperty.TryGetProperty("value", out var navigationDataValue))
                            {
                                var templatePath = await GetTemplateValue(navigationDataValue.GetString()!);

                                if (!string.IsNullOrEmpty(templatePath))
                                {
                                    infobox.Template = new Uri(new Uri("https://starwars.fandom.com/"), templatePath).ToString();
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

        return infobox.Data.Count > 0 || !string.IsNullOrEmpty(infobox.ImageUrl) || !string.IsNullOrEmpty(infobox.Template) ? infobox : null;
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

            return new HyperLink
            {
                Content = x.TextContent.Trim(' ', '"', ',', '.'),
                Href = href
            };
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
                }
            )
            .FirstOrDefault()
            ?.Href;
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

            var sections = new List<ArticleSection>(); // Get section metadata
            var sectionElements = parseElement.GetProperty("sections").EnumerateArray();

            // Handle different response formats for text content
            string? htmlContent = null;

            if (parseElement.TryGetProperty("text", out var textElement))
            {
                if (textElement.ValueKind == JsonValueKind.Object && textElement.TryGetProperty("*", out var contentElement))
                {
                    htmlContent = contentElement.GetString();
                }
                else if (textElement.ValueKind == JsonValueKind.String)
                {
                    htmlContent = textElement.GetString();
                }
            }

            if (string.IsNullOrEmpty(htmlContent))
                return sections;

            // Parse HTML with AngleSharp — one parse for the full page, no extra HTTP calls
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
                        PlainText = await StripHtmlAndCleanAsync(leadContent, cancellationToken),
                        Level = 0,
                        Links = await ExtractLinksAsync(leadContent, cancellationToken),
                    }
                );
            }

            // Split the already-parsed document into sections by heading elements
            // instead of making one HTTP request per section
            var headings = document.QuerySelectorAll("h1,h2,h3,h4,h5,h6").ToList();

            foreach (var sectionEl in sectionElements)
            {
                var sectionHeading = sectionEl.GetProperty("line").GetString() ?? "Unknown";

                var sectionLevel = sectionEl.GetProperty("level").ValueKind == JsonValueKind.String
                    ? int.Parse(sectionEl.GetProperty("level").GetString()!)
                    : sectionEl.GetProperty("level").GetInt32();

                // Find matching heading element — TextContent may include "[edit]" spans so use Contains
                var headingEl = headings.FirstOrDefault(h =>
                    h.TextContent.Trim().StartsWith(sectionHeading.Trim(), StringComparison.OrdinalIgnoreCase));

                if (headingEl == null) continue;

                // Collect sibling nodes until the next heading of same or higher level
                var contentParts = new List<string>();
                var sibling = headingEl.NextSibling;
                var headingTag = headingEl.TagName;

                while (sibling != null)
                {
                    if (sibling is IElement el && (el.TagName == headingTag ||
                        string.Compare(el.TagName, headingTag, StringComparison.OrdinalIgnoreCase) < 0 &&
                        el.TagName.Length == 2 && el.TagName[0] == 'H'))
                        break;

                    if (sibling is IElement contentEl)
                        contentParts.Add(contentEl.OuterHtml);

                    sibling = sibling.NextSibling;
                }

                var sectionContent = string.Join("\n", contentParts);

                if (!string.IsNullOrWhiteSpace(sectionContent))
                {
                    sections.Add(
                        new ArticleSection
                        {
                            Heading = sectionHeading,
                            Content = sectionContent,
                            PlainText = await StripHtmlAndCleanAsync(sectionContent, cancellationToken),
                            Level = sectionLevel,
                            Links = await ExtractLinksAsync(sectionContent, cancellationToken),
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

    async Task<string> StripHtmlAndCleanAsync(string html, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        try
        {
            using var doc = await _browsingContext.OpenAsync(req => req.Content(html), cancellationToken);
            var text = doc.Body?.TextContent ?? string.Empty;
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\[edit\]", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }
        catch
        {
            return html;
        }
    }

    async Task<List<string>> ExtractLinksAsync(string html, CancellationToken cancellationToken = default)
    {
        var links = new List<string>();
        if (string.IsNullOrEmpty(html))
            return links;

        try
        {
            using var doc = await _browsingContext.OpenAsync(req => req.Content(html), cancellationToken);

            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");

                if (!string.IsNullOrEmpty(href) && href.Contains("/wiki/"))
                {
                    var pageName = href.Split("/wiki/").LastOrDefault();

                    if (!string.IsNullOrEmpty(pageName))
                        links.Add(Uri.UnescapeDataString(pageName.Replace('_', ' ')));
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

            var parseElement = doc.RootElement.GetProperty("parse");

            // Handle different response formats for text content
            if (parseElement.TryGetProperty("text", out var textElement))
            {
                if (textElement.ValueKind == JsonValueKind.Object && textElement.TryGetProperty("*", out var contentElement))
                {
                    return contentElement.GetString() ?? string.Empty;
                }
                else if (textElement.ValueKind == JsonValueKind.String)
                {
                    return textElement.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
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
                .EnumerateArray()
                .Where(p => p.TryGetProperty("imageinfo", out _))
                .Select(p =>
                    {
                        var imageInfo = p.GetProperty("imageinfo")[0];
                        var title = p.GetProperty("title").GetString() ?? "Unknown";
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
                    }
                )
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch images for {Title}", title);
            return new List<MediaInfo>();
        }
    }

    async Task<DateTime> FetchPageInfoAsync(
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
                    ["rvprop"] = "timestamp",
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

            if (firstPage.TryGetProperty("revisions", out var revisions) && revisions.GetArrayLength() > 0)
            {
                var timestamp = revisions[0].GetProperty("timestamp").GetString();
                if (DateTime.TryParse(timestamp, out var lastModified))
                    return lastModified;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch page info for {Title}", title);
        }

        return DateTime.UtcNow;
    }

    /// <summary>
    /// Downloads a single wiki page by title, saves it to the raw pages DB, and optionally extracts its infobox.
    /// </summary>
    public async Task DownloadAndSavePageAsync(string title, CancellationToken cancellationToken = default)
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
        // Resume from last saved position if available
        var jobState = await _jobStateCollection
            .Find(s => s.JobName == PageDownloadJobName)
            .FirstOrDefaultAsync(cancellationToken);

        string? apContinue = jobState?.ContinueToken;

        if (apContinue != null)
            _logger.LogInformation("Resuming page download from saved position: {Token}", apContinue);
        else
            _logger.LogInformation("Starting Wookieepedia page download from the beginning");

        var syncedCount = 0;
        var failedCount = 0;

        try
        {
            do
            {
                var (pageTitles, nextContinue) = await GetPageTitlesBatchAsync(apContinue, cancellationToken);

                _logger.LogInformation("Processing batch of {Count} pages", pageTitles.Count);

                var (batchSynced, batchFailed) = await ProcessPageBatchAsync(pageTitles, cancellationToken);

                syncedCount += batchSynced;
                failedCount += batchFailed;

                apContinue = nextContinue;

                // Persist progress after every batch so a cancel can resume from here
                await _jobStateCollection.ReplaceOneAsync(
                    s => s.JobName == PageDownloadJobName,
                    new JobState { JobName = PageDownloadJobName, ContinueToken = apContinue, UpdatedAt = DateTime.UtcNow },
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken
                );

                _logger.LogInformation("Synced {Synced} pages so far (failed: {Failed})", syncedCount, failedCount);

            } while (apContinue != null && !cancellationToken.IsCancellationRequested);

            if (!cancellationToken.IsCancellationRequested)
            {
                // Job completed fully — clear the saved position
                await _jobStateCollection.DeleteOneAsync(s => s.JobName == PageDownloadJobName, CancellationToken.None);
                _logger.LogInformation("Page download complete. Synced: {Synced}, Failed: {Failed}", syncedCount, failedCount);


            }
            else
            {
                _logger.LogInformation("Page download cancelled. Progress saved — next run will resume from saved position.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during page download");
            throw;
        }
    }

    async Task<(int synced, int failed)> ProcessPageBatchAsync(
        List<string> pageTitles,
        CancellationToken cancellationToken
    )
    {
        using var semaphore = new SemaphoreSlim(8, 8);

        var tasks = pageTitles.Select(async title =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var pageDoc = await FetchPageAsync(title, cancellationToken);

                await _pagesCollection.ReplaceOneAsync(
                    Builders<Page>.Filter.Eq(p => p.PageId, pageDoc.PageId),
                    pageDoc,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken
                );
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

    // (Removed legacy sampling & infobox collection discovery methods to simplify configuration surface)

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
        "Out-of-universe articles",
        "Canceled projects",
        "Hoax articles",
        "Wookieepedia administration",
    ];

    Universe DetermineUniverse(List<string> categories)
    {
        if (categories.Any(c => OutOfUniverseMarkers.Any(m => c.Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            return Universe.OutOfUniverse;
        }

        return Universe.InUniverse;
    }
}