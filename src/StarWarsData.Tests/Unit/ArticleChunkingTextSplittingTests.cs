using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

/// <summary>
/// Pure text-splitting and boilerplate-stripping unit tests for
/// <see cref="ArticleChunkingService"/>. No MongoDB or embedding calls.
/// </summary>
[TestClass]
[TestCategory(TestTiers.Unit)]
public class ArticleChunkingTextSplittingTests
{
    [TestMethod]
    public void SplitByMarkdownHeadings_SingleSection_ReturnsContentWithIntroHeading()
    {
        var content = "Just some plain text without any headings. This is long enough to be a valid section with meaningful content for chunking.";
        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.AreEqual(1, sections.Count);
        Assert.IsTrue(sections[0].text.Contains("Just some plain text"));
    }

    [TestMethod]
    public void SplitByMarkdownHeadings_IntroAndHeadings_SplitsCorrectly()
    {
        var content = """
            This is the introduction paragraph.

            ## Biography
            Luke was born on Tatooine.

            ## Powers and abilities
            Luke was a powerful Jedi.

            ### Lightsaber combat
            He was skilled with a lightsaber.
            """;

        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.IsTrue(sections.Count >= 4);
        Assert.AreEqual("Introduction", sections[0].heading);
        Assert.AreEqual("Biography", sections[1].heading);
        Assert.AreEqual("Powers and abilities", sections[2].heading);
        Assert.AreEqual("Lightsaber combat", sections[3].heading);
    }

    [TestMethod]
    public void SplitByMarkdownHeadings_NoIntro_StartsWithFirstHeading()
    {
        var content = """
            ## Early life
            Born on Tatooine.

            ## Later life
            Became a Jedi.
            """;

        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.IsTrue(sections.Count >= 2);
        Assert.AreEqual("Early life", sections[0].heading);
    }

    [TestMethod]
    public void SplitByMarkdownHeadings_EmptyContent_ReturnsEmpty()
    {
        var sections = ArticleChunkingService.SplitByMarkdownHeadings("");
        Assert.AreEqual(0, sections.Count);
    }

    [TestMethod]
    public void SplitByMarkdownHeadings_WhitespaceOnly_ReturnsEmpty()
    {
        var sections = ArticleChunkingService.SplitByMarkdownHeadings("   \n\n  ");
        Assert.AreEqual(0, sections.Count);
    }

    [TestMethod]
    public void SplitByMarkdownHeadings_H1NotSplit_TreatedAsContent()
    {
        var content = "# Title\nSome content here.";
        var sections = ArticleChunkingService.SplitByMarkdownHeadings(content);

        Assert.AreEqual(1, sections.Count);
    }

    [TestMethod]
    public void SplitByParagraph_ShortText_BelowMinChunkChars_ReturnsEmpty()
    {
        var text = "A short paragraph.";
        var chunks = ArticleChunkingService.SplitByParagraph(text);
        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void SplitByParagraph_MediumText_ReturnsSingleChunk()
    {
        var text = "A paragraph that is long enough to pass the minimum chunk size threshold. " + "It needs to be at least 100 characters to be kept as a valid chunk for embedding.";
        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0]);
    }

    [TestMethod]
    public void SplitByParagraph_LargeText_SplitsIntoMultipleChunks()
    {
        var paragraphs = Enumerable.Range(1, 30).Select(i => $"Paragraph {i}: " + new string('x', 400)).ToList();
        var text = string.Join("\n\n", paragraphs);

        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.IsTrue(chunks.Count > 1, $"Expected multiple chunks but got {chunks.Count}");
        foreach (var c in chunks)
            Assert.IsTrue(c.Length >= 100, "Chunk should be above MinChunkChars");
    }

    [TestMethod]
    public void SplitByParagraph_NoParagraphBreaks_ReturnsSingleChunk()
    {
        var text = new string('x', 8000);
        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.AreEqual(1, chunks.Count);
    }

    [TestMethod]
    public void SplitByParagraph_SmallParagraphs_AreAggregated()
    {
        var paragraphs = Enumerable.Range(1, 10).Select(i => $"This is paragraph number {i} with enough content to matter for the chunking process.").ToList();
        var text = string.Join("\n\n", paragraphs);

        var chunks = ArticleChunkingService.SplitByParagraph(text);

        Assert.AreEqual(1, chunks.Count);
        foreach (var p in paragraphs)
            Assert.IsTrue(chunks[0].Contains(p));
    }

    [TestMethod]
    public void StripBoilerplate_RemovesBase64Images()
    {
        var text = "Some text [![alt](data:image/png;base64,abc123)](link) more text";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.IsFalse(result.Contains("data:image"));
        Assert.IsTrue(result.Contains("Some text"));
        Assert.IsTrue(result.Contains("more text"));
    }

    [TestMethod]
    public void StripBoilerplate_RemovesDataUris()
    {
        var text = "Before data:image/png;base64,longstringhere After";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.IsFalse(result.Contains("data:image"));
    }

    [TestMethod]
    public void StripBoilerplate_CollapsesExcessiveNewlines()
    {
        var text = "First paragraph\n\n\n\n\n\nSecond paragraph";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.IsFalse(result.Contains("\n\n\n"));
        Assert.IsTrue(result.Contains("First paragraph"));
        Assert.IsTrue(result.Contains("Second paragraph"));
    }

    [TestMethod]
    public void StripBoilerplate_PlainText_Unchanged()
    {
        var text = "Normal article content with no boilerplate.";
        var result = ArticleChunkingService.StripBoilerplate(text);

        Assert.AreEqual(text, result);
    }
}
