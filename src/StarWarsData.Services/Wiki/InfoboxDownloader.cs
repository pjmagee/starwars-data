using System.Collections.Specialized;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StarWarsData.Models;
using StarWarsData.Models.Mongo;
using StarWarsData.Services.Helpers;

namespace StarWarsData.Services.Wiki;

public class InfoboxDownloader
{
    private readonly ILogger<InfoboxDownloader> _logger;
    private readonly Settings _settings;
    private readonly HttpClient _httpClient;
    private readonly DirectoryInfo _dataDirectory;
    
    private static readonly char[] TrimValues = { '\'', '"', ',', '.', ':', '-' };
    private static readonly char[] TrimContents = { '\'', '"', ',', '.' };

    private static readonly Uri FandomUri = new Uri("https://starwars.fandom.com/");
    private static readonly Uri FandomWikiUri = new Uri(FandomUri, new Uri("/wiki/", UriKind.Relative));

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();
    
    private static readonly JsonSerializerOptions Options = new ()
    {
        WriteIndented = true,        
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    
    private bool _shouldContinue;
    private int _pwpcontinue = 1;
    
    public InfoboxDownloader(ILogger<InfoboxDownloader> logger, Settings settings, HttpClient httpClient)
    {
        _logger = logger;
        _settings = settings;
        _httpClient = httpClient;
        _dataDirectory = new DirectoryInfo(settings.DataDirectory);
        _pwpcontinue = _settings.PageStart;
    }
    
    private string GetPagesWithInfoboxes()
    {
        NameValueCollection queryString = HttpUtility.ParseQueryString(string.Empty);
        queryString.Add("action", "query");
        queryString.Add("list", "pageswithprop");
        queryString.Add("pwppropname", "infoboxes");
        queryString.Add("pwpprop", "ids|title|value");
        queryString.Add("pwplimit", $"{_settings.PageLimit}");
        queryString.Add("pwpcontinue", $"{_pwpcontinue}");
        queryString.Add("format", "json");
        return queryString.ToString()!;
    }
	
    public async Task ExecuteAsync(CancellationToken token)
    {
        do
        {
            using (var pages = await JsonDocument.ParseAsync(await _httpClient.GetStreamAsync("?" + GetPagesWithInfoboxes(), token), cancellationToken: token))
            {
                _shouldContinue = pages.RootElement.TryGetProperty("continue", out var continueElement);

                if (_shouldContinue)
                {
                    _pwpcontinue = int.Parse(continueElement.GetProperty("pwpcontinue").GetString()!);
                }

                if (_settings.FirstPageOnly)
                {
                    _shouldContinue = false;
                }

                if (pages.RootElement.TryGetProperty("query", out var queryElement) && queryElement.TryGetProperty("pageswithprop", out var pagesWithPropElement))
                {
                    var wikiPages = pagesWithPropElement.EnumerateArray().Where(x => x.GetProperty("ns").GetInt32().Equals(_settings.PageNamespace)).ToList();

                    var options = new ParallelOptions { CancellationToken = token };

                    await Parallel.ForEachAsync(wikiPages, options, async (wikiPage, t) =>
                    {
                        try
                        {
                            await ProcessWikiPage(wikiPage, t);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception, "error: {Error}", exception.Message);
                        }
                    });
                }
            }
        }
        while (_shouldContinue);
    }

    private async Task ProcessWikiPage(JsonElement page, CancellationToken cancellationToken)
    {
        Record record = new Record();
        
        if (page.TryGetProperty("pageid", out var pageid) && pageid.TryGetInt32(out int pageidValue))
        {
            record.PageId = pageidValue;
        }

        if (page.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
        {
            var titleValue = title.GetString()!;
            record.PageUrl = new Uri(FandomWikiUri, titleValue.Replace(" ", "_")).ToString();
        }

        if (page.TryGetProperty("value", out var infoboxJsonValue) && infoboxJsonValue.ValueKind == JsonValueKind.String)
        {
            using (var valueDocument = JsonDocument.Parse(infoboxJsonValue.GetString()!))
            {
                var infobox = valueDocument.RootElement.EnumerateArray().FirstOrDefault();

                if (infobox.ValueKind == JsonValueKind.Object && infobox.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProperty) && item.TryGetProperty("data", out var dataProperty) && typeProperty.ValueKind == JsonValueKind.String)
                        {
                            if (typeProperty.ValueEquals("image") && dataProperty.ValueKind == JsonValueKind.Array)
                            {
                                var imageElement = dataProperty.EnumerateArray().FirstOrDefault();

                                if (imageElement.ValueKind == JsonValueKind.Object && imageElement.TryGetProperty("url", out var url))
                                {
                                    record.ImageUrl = url.GetString()!;
                                }
                            }
                            else if (typeProperty.ValueEquals("title") && dataProperty.TryGetProperty("value", out var titleDataValue))
                            {
                                record.Data.Add(new InfoboxProperty("Titles", await GetDataValue(titleDataValue.GetString()!)));
                            }
                            else if (typeProperty.ValueEquals("group") && dataProperty.TryGetProperty("value", out var groupDataValue) && groupDataValue.ValueKind == JsonValueKind.Array)
                            {
                                var dataValues = groupDataValue
                                    .EnumerateArray()
                                    .Where(x => x.TryGetProperty("type", out var t) &&
                                                t.ValueKind == JsonValueKind.String && t.ValueEquals("data") &&
                                                x.TryGetProperty("data", out var d) &&
                                                d.ValueKind == JsonValueKind.Object);
                                
                                foreach (var dataValueItem in dataValues)
                                {
                                    if (dataValueItem.TryGetProperty("data", out var dataValueItemData) && dataValueItemData.ValueKind == JsonValueKind.Object)
                                    {
                                        if (dataValueItemData.TryGetProperty("label", out var label) &&
                                            label.ValueKind == JsonValueKind.String && !label.ValueEquals(string.Empty) &&
                                            dataValueItemData.TryGetProperty("value", out var value) &&
                                            value.ValueKind == JsonValueKind.String)
                                        {
                                            record.Data.Add(new InfoboxProperty(await GetLabelValue(label.GetString()!), await GetDataValue(value.GetString()!)));
                                        }
                                    }
                                }
                            }
                            else if (typeProperty.ValueEquals("navigation") && dataProperty.TryGetProperty("value", out var navigationDataValue))
                            {
                                record.TemplateUrl = new Uri(FandomUri, await GetTemplateValue(navigationDataValue.GetString()!)).ToString();
                            }
                        }
                    }
                }
            }
        }

        await Save(record, cancellationToken);
    }

    private async Task<string> GetLabelValue(string label)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(label));
        doc.QuerySelectorAll("sup").Do(sup => sup.Remove());
        return doc.Body.TextContent.Trim(TrimContents);
    }

    private async Task<string?> GetTemplateValue(string html)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(html.Replace("\\", string.Empty)));
        
        return doc
            .QuerySelectorAll("a")
            .Select(x => new HyperLink { Content = x.TextContent.Trim(TrimContents), Href = x.GetAttribute("href") })
            .FirstOrDefault()?.Href;
    }
    
    private async Task<DataValue> GetDataValue(string html)
    {
        using var context = BrowsingContext.New();
        using var doc = await context.OpenAsync(r => r.Content(html.Replace("\\", string.Empty)));
        doc.QuerySelectorAll("sup").Do(sup => sup.Remove());
        doc.QuerySelectorAll("br").Do(br => br.Insert(AdjacentPosition.BeforeBegin, "\n"));
        doc.QuerySelectorAll("li").Do(li => li.Insert(AdjacentPosition.BeforeBegin, "\n"));

        HyperLink LinkSelector(IElement x)
        {
            var href = x.GetAttribute("href");

            if (href.StartsWith("/wiki/"))
            {
                href = new Uri(FandomUri, href).ToString();
            }
                    
            return new HyperLink
            {
                Content = x.TextContent.Trim(TrimContents), 
                Href = href
            };
        }

        return new DataValue
        {
            Values = doc.Body
                .Text()
                .Split('\n')
                .Concat(doc.QuerySelectorAll("li").SelectMany(x => x.Text().Split('\n')))
                .Select(x => x.Trim(TrimValues)).Where(line => !string.IsNullOrWhiteSpace(line)).Distinct().ToList(),
                    
            Links = doc.QuerySelectorAll("a")
                .Select(LinkSelector)
                .ToList()
        };
    }

    private async Task Save(Record record, CancellationToken token)
    {
        var fileTitle = record.PageTitle;
        var fileId = record.PageId;
        var template = record.Template?.Split(':').Last()!;
        Array.ForEach(InvalidChars, c => fileTitle = fileTitle.Replace(c.ToString(), " ").Trim());

        var path = Path.Combine(_dataDirectory.FullName, template!, $"{fileId} - {template} - {fileTitle}.json");

        var dirPath = Path.Combine($"{_dataDirectory.FullName}", template);
        
        if (Directory.Exists(dirPath))
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, Options), token);
        }
        else
        {
            Directory.CreateDirectory(dirPath);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record, Options), token);
        }
        
        _logger.LogInformation("Saved {Path}", path);
    }
}