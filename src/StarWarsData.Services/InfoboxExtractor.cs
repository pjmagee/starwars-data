using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using StarWarsData.Models;
using StarWarsData.Models.Entities;

namespace StarWarsData.Services;

public class InfoboxExtractor
{
    readonly ILogger<InfoboxExtractor> _logger;
    readonly SettingsOptions _settingsOptions;
    readonly IMongoDatabase _pagesDb;
    readonly IMongoDatabase _infoboxDb;
    readonly IMongoCollection<Page> _pagesCollection;

    public InfoboxExtractor(
        ILogger<InfoboxExtractor> logger,
        IOptions<SettingsOptions> settingsOptions,
        IMongoClient mongoClient
    )
    {
        _logger = logger;
        _settingsOptions = settingsOptions.Value;
        _pagesDb = mongoClient.GetDatabase(_settingsOptions.PagesDb);
        _infoboxDb = mongoClient.GetDatabase(_settingsOptions.PageInfoboxDb);
        _pagesCollection = _pagesDb.GetCollection<Page>("Pages");
    }

    public async Task ExtractInfoboxesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting infobox extraction from Pages collection");

        var processedCount = 0;
        var errorCount = 0;
        try
        {
            var totalPages = await _pagesCollection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Total pages in source collection: {TotalPages}", totalPages);

            // Correct filter: pages where infobox object exists (not null)
            var filter = Builders<Page>.Filter.Ne(p => p.Infobox, null);
            var totalWithInfobox = await _pagesCollection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
            var withoutInfobox = totalPages - totalWithInfobox;

            _logger.LogInformation("Pages with infobox: {WithInfobox} | Without infobox: {WithoutInfobox}", totalWithInfobox, withoutInfobox);

            if (totalPages == 0)
            {
                _logger.LogWarning("No pages present. Run the page download job first.");
                return;
            }

            if (totalWithInfobox == 0)
            {
                // Sample a few page titles to help diagnose why infobox missing
                var sample = await _pagesCollection.Find(Builders<Page>.Filter.Empty).Limit(5).Project(x => new { x.PageId, x.Title }).ToListAsync(cancellationToken);
                _logger.LogWarning("No pages found with embedded infobox objects. Sample pages: {Sample}", string.Join(", ", sample.Select(s => $"{s.PageId}:{s.Title}")));
                return; // Nothing to do
            }

            _logger.LogInformation("Found {Total} pages containing infobox data to process", totalWithInfobox);

            // Stream through pages to avoid loading all into memory
            using var cursor = await _pagesCollection.Find(filter).ToCursorAsync(cancellationToken);
            while (await cursor.MoveNextAsync(cancellationToken))
            {
                foreach (var page in cursor.Current)
                {
                    try
                    {
                        await ProcessPageInfobox(page, cancellationToken);
                        processedCount++;
                        if (processedCount % 100 == 0)
                        {
                            _logger.LogInformation("Processed {Processed}/{Total} pages with infoboxes", processedCount, totalWithInfobox);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to process infobox for page {PageId}: {Title}", page.PageId, page.Title);
                        errorCount++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during infobox extraction");
            throw;
        }

        _logger.LogInformation("Infobox extraction completed. Processed: {ProcessedCount}, Errors: {ErrorCount}", processedCount, errorCount);
    }

    async Task ProcessPageInfobox(Page page, CancellationToken cancellationToken)
    {
        if (page.Infobox == null || (page.Infobox.Data.Count == 0 && string.IsNullOrEmpty(page.Infobox.ImageUrl) && string.IsNullOrEmpty(page.Infobox.Template)))
        {
            return; // nothing meaningful
        }

        var collectionName = SanitizeTemplateName(page.Infobox.Template);

        var targets = _settingsOptions.TargetTemplates.ToList();
        if (targets.Count > 0 && !targets.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping PageId {PageId} — template '{Template}' not in TargetTemplates", page.PageId, collectionName);
            return;
        }

        var excluded = _settingsOptions.ExcludedTemplates.ToList();
        if (excluded.Count > 0 && excluded.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping PageId {PageId} — template '{Template}' is in ExcludedTemplates", page.PageId, collectionName);
            return;
        }

        var infobox = new Infobox
        {
            PageId = page.PageId,
            WikiUrl = page.WikiUrl,
            Template = page.Infobox.Template ?? "Template:Unknown",
            ImageUrl = page.Infobox.ImageUrl,
            Data = page.Infobox.Data,
            Relationships = [],
            Continuity = page.Continuity,
            PageTitle = page.Title
        };

        await SaveInfobox(infobox, cancellationToken);
    }

    async Task SaveInfobox(Infobox infobox, CancellationToken cancellationToken)
    {
        try
        {
            var collectionName = SanitizeTemplateName(infobox.Template);
            var collection = _infoboxDb.GetCollection<Infobox>(collectionName);
            var filter = Builders<Infobox>.Filter.Eq(x => x.PageId, infobox.PageId);
            var options = new ReplaceOptions { IsUpsert = true };

            await collection.ReplaceOneAsync(filter, infobox, options, cancellationToken);

            _logger.LogDebug(
                "Saved infobox for PageId {PageId} - {PageTitle} to collection {Collection}",
                infobox.PageId, infobox.PageTitle, collectionName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save infobox for PageId {PageId}: {ErrorMessage}",
                infobox.PageId, ex.Message);
            throw;
        }
    }

    static string SanitizeTemplateName(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Unknown";

        var working = template;
        var wikiIdx = working.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
        if (wikiIdx >= 0)
        {
            working = working[(wikiIdx + 6)..];
        }

        var lastColon = working.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < working.Length - 1)
        {
            working = working[(lastColon + 1)..];
        }

        working = working.Split('?', '#')[0];
        return string.IsNullOrWhiteSpace(working) ? "Unknown" : working.Trim();
    }
}
