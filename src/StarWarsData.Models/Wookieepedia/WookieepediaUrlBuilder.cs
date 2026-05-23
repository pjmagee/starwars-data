using StarWarsData.Models.Entities;

namespace StarWarsData.Models.Wookieepedia;

public static class WookieepediaUrlBuilder
{
    const string WikiBase = "https://starwars.fandom.com/wiki/";

    // Same-origin proxy on the Frontend that fetches article HTML from Fandom's
    // MediaWiki API, strips chrome, and wraps it in a clean stylesheet. Direct
    // iframe embeds of starwars.fandom.com hit a Cloudflare bot challenge and
    // unstyled `?action=render` output looks unreadable — Design-043 § rendering.
    const string ProxyPath = "/wookieepedia/article";

    const string LegendsSuffix = "/Legends";

    /// <summary>
    /// Build the iframe src — a SAME-ORIGIN URL pointing at the Frontend's
    /// Wookieepedia article proxy. The browser loads cleaned styled HTML from
    /// our server (no Fandom chrome, no Cloudflare challenge in the iframe).
    /// </summary>
    public static string BuildRenderUrl(string canonicalTitle, Continuity continuity)
    {
        var normalised = NormaliseTitle(canonicalTitle).Replace(' ', '_');
        var titleWithSuffix = continuity == Continuity.Legends ? normalised + LegendsSuffix : normalised;
        return $"{ProxyPath}?title={Uri.EscapeDataString(titleWithSuffix)}";
    }

    /// <summary>
    /// Build the canonical (full-page, with Fandom chrome) URL — used for the
    /// "Open on Wookieepedia" affordance and the modal footer link.
    /// </summary>
    public static Uri BuildCanonicalUrl(string canonicalTitle, Continuity continuity) => new(WikiBase + EncodeTitleForWikiPath(canonicalTitle, continuity));

    public static string NormaliseTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var trimmed = title.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Title must not be empty or whitespace.", nameof(title));
        return trimmed;
    }

    // Wiki path: `/Legends` stays literal so the slash is preserved as a path separator.
    static string EncodeTitleForWikiPath(string title, Continuity continuity)
    {
        var withUnderscores = NormaliseTitle(title).Replace(' ', '_');
        var encoded = Uri.EscapeDataString(withUnderscores).Replace("%27", "'");
        return continuity == Continuity.Legends ? encoded + LegendsSuffix : encoded;
    }
}
