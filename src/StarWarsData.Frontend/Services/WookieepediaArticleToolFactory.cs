using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using StarWarsData.Models.Entities;
using StarWarsData.Models.Wookieepedia;

namespace StarWarsData.Frontend.Services;

public sealed class WookieepediaArticleToolFactory : IGlobalCopilotToolFactory
{
    const string ModelFacingDescription = """
        Open the Wookieepedia article for a subject in an in-app modal. The modal shows the
        article body only (no Fandom site chrome). Use this when the user asks to "show",
        "open", "pop up", or "pull up" a Wookieepedia article, or expresses a desire to read
        the underlying source for an entity.

        Argument guidance: prefer `pageId` when you have one from a prior keyword_search or
        search_wiki_pages result — it resolves deterministically. Otherwise pass `title` as
        the canonical Wookieepedia page title (e.g. "Coruscant", "Darth Maul", "Battle of
        Yavin") — what the user would type into Wookieepedia's search box.

        After this tool returns, narrate ONE short sentence about what you opened.
        Do NOT echo the tool name or the raw return string in your prose.
        """;

    readonly WookieepediaArticleModalService _modalService;
    readonly JsonSerializerOptions _serializerOptions;

    public WookieepediaArticleToolFactory(WookieepediaArticleModalService modalService, IOptions<JsonOptions> jsonOptions)
    {
        _modalService = modalService;
        _serializerOptions = jsonOptions.Value.SerializerOptions;
    }

    public PageAction CreateAction()
    {
        var tool = AIFunctionFactory.Create(OpenWookieepediaArticleAsync, ToolNames.Sp4OpenWookieepediaArticle, ModelFacingDescription, serializerOptions: _serializerOptions);

        return new PageAction(tool, Label: "Open a Wookieepedia article", Example: "show me the Wookieepedia article for Coruscant");
    }

    async Task<string> OpenWookieepediaArticleAsync(int? pageId, string? title, CancellationToken cancellationToken)
    {
        var requested = pageId is not null ? $"#{pageId}" : (title ?? "(none)");
        var resolved = await _modalService.OpenAsync(new WookieepediaArticleRequest(pageId, title?.Trim()), cancellationToken);

        if (!resolved.IsSuccess)
            return $"Article not found for '{requested}'";

        return resolved.Continuity == Continuity.Legends ? $"Opened article: {resolved.Title} (Legends)" : $"Opened article: {resolved.Title}";
    }
}
