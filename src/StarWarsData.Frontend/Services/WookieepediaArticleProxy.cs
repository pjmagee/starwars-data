using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace StarWarsData.Frontend.Services;

/// <summary>
/// Same-origin proxy that serves cleaned Wookieepedia article HTML to the
/// modal iframe. Direct browser embeds of starwars.fandom.com hit a Cloudflare
/// bot challenge; the server-side fetch + clean + reserve approach sidesteps that
/// and gives us full control over typography and chrome stripping.
///
/// Endpoint: GET /wookieepedia/article?title=&lt;title&gt;
/// Returns: text/html — a self-contained HTML doc with our stylesheet, the
/// article body, and Fandom chrome (TOC, navboxes, edit links, message boxes)
/// stripped.
/// </summary>
public static partial class WookieepediaArticleProxy
{
    // A real-looking but honest UA. Cloudflare's default rules accept this;
    // a request with no UA or a suspicious one would be challenged.
    const string UserAgent = "Mozilla/5.0 (compatible; StarWarsData/0.1; +https://github.com/pjmagee/starwars-data)";

    public static void MapWookieepediaArticleProxy(this WebApplication app)
    {
        app.MapGet("/wookieepedia/article", HandleAsync);
    }

    static async Task<IResult> HandleAsync([FromQuery] string title, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("WookieepediaArticleProxy");

        if (string.IsNullOrWhiteSpace(title) || title.Length > 250)
            return Results.BadRequest("title required (max 250 chars)");

        var http = httpClientFactory.CreateClient("Wookieepedia");
        var apiUrl =
            $"https://starwars.fandom.com/api.php?action=parse"
            + $"&page={Uri.EscapeDataString(title.Trim())}"
            + $"&prop=text%7Cdisplaytitle"
            + $"&redirects=1"
            + // follow redirects (e.g. Darth_Maul -> Maul)
            $"&disableeditsection=1"
            + $"&disabletoc=1"
            + $"&format=json&formatversion=2";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Wookieepedia fetch failed: {Status} for title {Title}", resp.StatusCode, title);
                return Results.NotFound();
            }

            var payload = await resp.Content.ReadFromJsonAsync<MediaWikiParseEnvelope>(cancellationToken);
            if (payload?.Parse is null)
            {
                logger.LogDebug("Wookieepedia returned no parse payload for {Title}", title);
                return Results.NotFound();
            }

            var body = SanitizeArticleHtml(payload.Parse.Text ?? string.Empty);
            // displaytitle may contain HTML (e.g. <span class="mw-page-title-main">…</span>
            // or <i>…</i> for italicised titles). Strip tags then decode entities to get a
            // safe plain-text title we can re-encode for the <h1>.
            var rawDisplayTitle = payload.Parse.DisplayTitle ?? title;
            var displayTitle = WebUtility.HtmlDecode(StripTagsRegex().Replace(rawDisplayTitle, string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(displayTitle))
                displayTitle = title;
            var html = WrapInDocument(body, displayTitle);

            return Results.Content(html, "text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wookieepedia proxy error for {Title}", title);
            return Results.Problem(title: "Failed to fetch article", detail: "Wookieepedia returned an error or could not be reached.", statusCode: StatusCodes.Status502BadGateway);
        }
    }

    sealed record MediaWikiParseEnvelope(MediaWikiParse? Parse);

    sealed record MediaWikiParse(string? Text, string? DisplayTitle);

    // Regex patterns to strip Fandom/MediaWiki chrome that survives `prop=text`.
    // These are conservative — only well-known chrome classes get removed; the
    // article body, infoboxes, and meaningful content stay.
    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTagRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex StyleTagRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<span\s+class=""[^""]*\bmw-editsection\b[^""]*""[^>]*>.*?</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex EditSectionRegex();

    [GeneratedRegex(@"<table\s+class=""[^""]*\b(navbox|ambox|messagebox|metadata|navigation-not-searchable)\b[^""]*""[^>]*>.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ChromeTableRegex();

    [GeneratedRegex(
        @"<div\s+(?:id=""[^""]*""\s+)?class=""[^""]*\b(navbox|toc|noprint|interlanguage-links|categories|catlinks|printfooter|mw-jump-link|mw-indicators|message|hidable-content|hidable-button|appearancelist|app-template)\b[^""]*""[^>]*>.*?</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase
    )]
    private static partial Regex ChromeDivRegex();

    // Wookieepedia top-of-page chrome: the Canon|Legends tab navigation table
    // (renders as broken text without Fandom's JS) and the small floating
    // era-icon strip in the upper right. Both have stable Wookieepedia IDs.
    [GeneratedRegex(@"<table\s+[^>]*id=""canontab""[^>]*>.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CanonTabsRegex();

    [GeneratedRegex(@"<div\s+[^>]*id=""title-eraicons""[^>]*>.*?</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex EraIconsRegex();

    // Orphaned inline citation superscripts like <sup class="reference">[164]</sup>
    // — point at footnote anchors in the references block that we truncate below.
    [GeneratedRegex(@"<sup\s+[^>]*class=""[^""]*\breference\b[^""]*""[^>]*>.*?</sup>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineCitationRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex StripTagsRegex();

    // Appendix section IDs the article body uses. Once we encounter the
    // <h2> introducing any of these, everything from that H2 to end-of-doc is
    // dropped — that's typically 60%+ of the byte size and 100% of the visual
    // clutter (massive appearance lists, broken footnote links, etc.).
    static readonly string[] AppendixSectionIds = ["Appearances", "Sources", "Notes_and_references", "References", "External_links", "In_other_languages"];

    static string SanitizeArticleHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        html = ScriptTagRegex().Replace(html, string.Empty);
        html = StyleTagRegex().Replace(html, string.Empty);
        html = CommentRegex().Replace(html, string.Empty);
        html = EditSectionRegex().Replace(html, string.Empty);
        html = ChromeTableRegex().Replace(html, string.Empty);
        html = ChromeDivRegex().Replace(html, string.Empty);
        html = CanonTabsRegex().Replace(html, string.Empty);
        html = EraIconsRegex().Replace(html, string.Empty);
        html = InlineCitationRegex().Replace(html, string.Empty);
        html = TruncateAtAppendix(html);
        return html;
    }

    /// <summary>
    /// Find the earliest appendix section heading (Appearances, Sources, etc.)
    /// and truncate the HTML at the start of its containing &lt;h2&gt;. Returns
    /// the original HTML if no appendix section is found.
    /// </summary>
    static string TruncateAtAppendix(string html)
    {
        var earliestH2Start = html.Length;
        foreach (var sectionId in AppendixSectionIds)
        {
            // mw-headline span carries the section id: <span class="mw-headline" id="Appearances">
            var idMatch = Regex.Match(html, $@"\bid\s*=\s*[""']{Regex.Escape(sectionId)}[""']", RegexOptions.IgnoreCase);
            if (!idMatch.Success)
                continue;

            // Walk back to find the opening <h2 that contains this id.
            var slice = html.AsSpan(0, idMatch.Index);
            var h2Start = slice.LastIndexOf("<h2", StringComparison.OrdinalIgnoreCase);
            if (h2Start < 0)
                continue;
            if (h2Start < earliestH2Start)
                earliestH2Start = h2Start;
        }
        return earliestH2Start < html.Length ? html[..earliestH2Start] : html;
    }

    static string WrapInDocument(string body, string displayTitle) =>
        $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="referrer" content="no-referrer">
                <base href="https://starwars.fandom.com/wiki/" target="_blank">
                <title>{{WebUtility.HtmlEncode(displayTitle)}}</title>
                <style>
                    :root { color-scheme: dark; }
                    body {
                        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Oxygen, Ubuntu, sans-serif;
                        background: #1a1a2e;
                        color: #e0e0e0;
                        margin: 0;
                        padding: 24px 48px 64px;
                        line-height: 1.65;
                        font-size: 16px;
                    }
                    a { color: #66bb6a; text-decoration: none; }
                    a:hover { text-decoration: underline; }
                    a.new { color: #ef5350; }
                    a.external { color: #ffa726; }
                    a.external::after { content: " \2197"; font-size: 0.8em; opacity: 0.7; }
                    h1, h2, h3, h4, h5, h6 {
                        color: #ffa726;
                        font-weight: 600;
                        margin-top: 1.5em;
                        margin-bottom: 0.5em;
                        line-height: 1.25;
                    }
                    h1 { font-size: 2.2em; border-bottom: 2px solid #ffa72655; padding-bottom: 8px; margin-top: 0; }
                    h2 { font-size: 1.7em; border-bottom: 1px solid #ffa72633; padding-bottom: 4px; }
                    h3 { font-size: 1.35em; }
                    h4 { font-size: 1.15em; }
                    p { margin: 0.85em 0; }
                    img { max-width: 100%; height: auto; border-radius: 4px; }
                    figure { margin: 16px 0; }
                    figcaption { font-size: 0.9em; color: #aaa; font-style: italic; text-align: center; margin-top: 4px; }
                    /* Portable infobox (Wookieepedia's main infobox structure) */
                    aside.portable-infobox {
                        float: right;
                        margin: 0 0 20px 24px;
                        padding: 0;
                        background: #16213e;
                        border: 1px solid #ffa72644;
                        border-radius: 8px;
                        max-width: 340px;
                        font-size: 0.92em;
                        overflow: hidden;
                    }
                    aside.portable-infobox .pi-title {
                        color: #ffa726;
                        font-size: 1.15em;
                        font-weight: 700;
                        padding: 10px 14px;
                        background: linear-gradient(180deg, #1f2a4d, #16213e);
                        border-bottom: 1px solid #ffa72633;
                    }
                    aside.portable-infobox .pi-image, aside.portable-infobox figure { margin: 0; }
                    aside.portable-infobox .pi-image img { width: 100%; max-width: none; border-radius: 0; }
                    aside.portable-infobox .pi-header,
                    aside.portable-infobox .pi-section-navigation {
                        background: #1f2a4d;
                        color: #ffa726;
                        font-weight: 600;
                        padding: 6px 14px;
                        border-bottom: 1px solid #ffa72622;
                        border-top: 1px solid #ffa72622;
                        text-align: center;
                    }
                    aside.portable-infobox .pi-data {
                        display: flex;
                        padding: 6px 14px;
                        border-bottom: 1px solid #ffa72611;
                    }
                    aside.portable-infobox .pi-data:last-child { border-bottom: 0; }
                    aside.portable-infobox .pi-data-label { font-weight: 600; flex: 0 0 38%; color: #cccccc; padding-right: 8px; }
                    aside.portable-infobox .pi-data-value { flex: 1; color: #e0e0e0; }
                    /* Legacy table-based infobox fallback */
                    table.infobox {
                        float: right;
                        margin: 0 0 16px 24px;
                        border-collapse: collapse;
                        background: #16213e;
                        border: 1px solid #ffa72644;
                        border-radius: 6px;
                        overflow: hidden;
                        max-width: 340px;
                    }
                    table.infobox caption { color: #ffa726; font-weight: 700; padding: 8px; }
                    table.infobox th, table.infobox td { padding: 6px 10px; border-bottom: 1px solid #ffa72611; vertical-align: top; }
                    /* Regular tables */
                    table { border-collapse: collapse; margin: 12px 0; }
                    table th, table td { border: 1px solid #ffa72633; padding: 6px 10px; }
                    table th { background: #16213e; color: #ffa726; }
                    /* Lists */
                    ul, ol { padding-left: 1.6em; }
                    li { margin: 4px 0; }
                    /* Quotes */
                    blockquote {
                        border-left: 4px solid #ffa72677;
                        margin: 16px 0;
                        padding: 8px 16px;
                        background: rgba(22, 33, 62, 0.45);
                        font-style: italic;
                    }
                    .quote, .pull-quote {
                        border-left: 4px solid #ffa72677;
                        padding: 8px 16px;
                        margin: 16px 0;
                        background: rgba(22, 33, 62, 0.45);
                        font-style: italic;
                    }
                    code, pre {
                        background: #16213e;
                        padding: 2px 6px;
                        border-radius: 3px;
                        font-size: 0.9em;
                    }
                    pre { padding: 12px; overflow-x: auto; }
                    /* References block at the bottom of the article */
                    .references, ol.references, .reflist {
                        font-size: 0.85em;
                        color: #aaa;
                        border-top: 1px solid #ffa72622;
                        padding-top: 12px;
                        margin-top: 24px;
                    }
                    .reference { font-size: 0.75em; vertical-align: super; }
                    /* Belt-and-braces: hide any chrome the regex missed */
                    .mw-editsection, .editsection, .noprint,
                    .navbox, .ambox, .messagebox, .metadata,
                    .printfooter, .catlinks, .mw-jump-link,
                    .mw-indicators, .siteSub, .navigation-not-searchable,
                    .toc, .toctitle, .vector-toc, .interlanguage-links,
                    .nv-talk, .nv-edit, .quote-mark {
                        display: none !important;
                    }
                    hr { border: 0; border-top: 1px solid #ffa72633; margin: 24px 0; }
                    /* Responsive: collapse infobox below content on narrow viewports */
                    @media (max-width: 720px) {
                        body { padding: 16px; }
                        aside.portable-infobox, table.infobox {
                            float: none;
                            margin: 16px 0;
                            max-width: 100%;
                        }
                        h1 { font-size: 1.8em; }
                        h2 { font-size: 1.4em; }
                    }
                </style>
            </head>
            <body>
                <h1>{{WebUtility.HtmlEncode(displayTitle)}}</h1>
                {{body}}
            </body>
            </html>
            """;
}
