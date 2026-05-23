using StarWarsData.Models.Entities;
using StarWarsData.Models.Wookieepedia;

namespace StarWarsData.Tests.Unit.Frontend;

[TestClass]
[TestCategory(TestTiers.Unit)]
public class WookieepediaUrlBuilderTests
{
    const string ProxyPath = "/wookieepedia/article?title=";
    const string WikiBase = "https://starwars.fandom.com/wiki/";

    [TestMethod]
    public void BuildRenderUrl_Canon_UsesSameOriginProxy() => Assert.AreEqual(ProxyPath + "Coruscant", WookieepediaUrlBuilder.BuildRenderUrl("Coruscant", Continuity.Canon));

    [TestMethod]
    public void BuildRenderUrl_Legends_AppendsEncodedSuffix()
    {
        // The proxy treats the title as a single query-string value, so the slash
        // before "Legends" MUST be percent-encoded as part of the title.
        var url = WookieepediaUrlBuilder.BuildRenderUrl("Coruscant", Continuity.Legends);
        Assert.AreEqual(ProxyPath + "Coruscant%2FLegends", url);
    }

    [TestMethod]
    public void BuildRenderUrl_Both_DefaultsToCanon() => Assert.AreEqual(ProxyPath + "Coruscant", WookieepediaUrlBuilder.BuildRenderUrl("Coruscant", Continuity.Both));

    [TestMethod]
    public void BuildRenderUrl_Unknown_DefaultsToCanon() => Assert.AreEqual(ProxyPath + "Coruscant", WookieepediaUrlBuilder.BuildRenderUrl("Coruscant", Continuity.Unknown));

    [TestMethod]
    public void BuildRenderUrl_SpacesBecomeUnderscores() => Assert.AreEqual(ProxyPath + "Darth_Maul", WookieepediaUrlBuilder.BuildRenderUrl("Darth Maul", Continuity.Canon));

    [TestMethod]
    public void BuildRenderUrl_NonAsciiPercentEncoded()
    {
        Assert.AreEqual(ProxyPath + "Bib_Fortuna", WookieepediaUrlBuilder.BuildRenderUrl("Bib Fortuna", Continuity.Canon));
        StringAssert.Contains(WookieepediaUrlBuilder.BuildRenderUrl("Café", Continuity.Canon), "Caf%C3%A9");
    }

    [TestMethod]
    public void BuildCanonicalUrl_WikiPath_NoSuffix()
    {
        var uri = WookieepediaUrlBuilder.BuildCanonicalUrl("Coruscant", Continuity.Canon);
        Assert.AreEqual(WikiBase + "Coruscant", uri.ToString());
    }

    [TestMethod]
    public void BuildCanonicalUrl_LegendsKeepsLiteralSlash()
    {
        // Wiki path: /Legends stays a literal slash — it's a path separator, not part of the title.
        var uri = WookieepediaUrlBuilder.BuildCanonicalUrl("Coruscant", Continuity.Legends);
        Assert.AreEqual(WikiBase + "Coruscant/Legends", uri.ToString());
    }

    [TestMethod]
    public void NormaliseTitle_TrimsWhitespace() => Assert.AreEqual("Coruscant", WookieepediaUrlBuilder.NormaliseTitle("  Coruscant  "));

    [TestMethod]
    public void NormaliseTitle_EmptyThrows() => Assert.ThrowsExactly<ArgumentException>(() => WookieepediaUrlBuilder.NormaliseTitle(""));

    [TestMethod]
    public void NormaliseTitle_AllWhitespaceThrows() => Assert.ThrowsExactly<ArgumentException>(() => WookieepediaUrlBuilder.NormaliseTitle("   "));

    [TestMethod]
    public void NormaliseTitle_NullThrows() => Assert.ThrowsExactly<ArgumentNullException>(() => WookieepediaUrlBuilder.NormaliseTitle(null!));

    [TestMethod]
    public void BuildRenderUrl_TrimsInputTitle() => Assert.AreEqual(ProxyPath + "Coruscant", WookieepediaUrlBuilder.BuildRenderUrl("  Coruscant  ", Continuity.Canon));
}
