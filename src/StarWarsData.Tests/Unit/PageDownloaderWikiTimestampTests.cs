using System.Text.Json;
using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Regression coverage for the incremental-sync timestamp bug shipped 2026-03-18
/// (commit 1e5018557a) and found 2026-05-18: <c>DateTime.ToString("o")</c> emits 7
/// fractional-second digits, which MediaWiki's <c>allrevisions</c> API rejects with
/// <c>badtimestamp</c>. The error came back as HTTP 200, so the sync silently found
/// "0 changed pages" every run while still advancing its watermark — the wiki had
/// not been synced for ~2 months despite the job "running" daily.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class PageDownloaderWikiTimestampTests
{
    [TestMethod]
    public void ToWikiTimestamp_HasNoFractionalSeconds()
    {
        var dt = new DateTime(2026, 5, 18, 12, 0, 0, 123, DateTimeKind.Utc);

        var formatted = PageDownloader.ToWikiTimestamp(dt);

        Assert.AreEqual("2026-05-18T12:00:00Z", formatted);
        Assert.IsFalse(formatted.Contains('.'), "MediaWiki rejects fractional seconds with 'badtimestamp'.");
    }

    [TestMethod]
    public void ToWikiTimestamp_NormalisesNonUtcToUtc()
    {
        // Local kind must be coerced to UTC so the wiki query window stays correct.
        var local = new DateTime(2026, 5, 18, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();

        var formatted = PageDownloader.ToWikiTimestamp(local);

        Assert.AreEqual("2026-05-18T12:00:00Z", formatted);
    }

    [TestMethod]
    public void ThrowIfApiError_ThrowsOnMediaWikiErrorBody()
    {
        // MediaWiki returns errors as HTTP 200 + this body; the sync used to swallow it.
        using var doc = JsonDocument.Parse("""{"error":{"code":"badtimestamp","info":"Invalid value for timestamp parameter \"arvstart\"."}}""");

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => PageDownloader.ThrowIfApiError(doc));
        StringAssert.Contains(ex.Message, "badtimestamp");
    }

    [TestMethod]
    public void ThrowIfApiError_NoThrowOnSuccessBody()
    {
        using var doc = JsonDocument.Parse("""{"query":{"allrevisions":[]}}""");

        PageDownloader.ThrowIfApiError(doc); // must not throw
    }
}
