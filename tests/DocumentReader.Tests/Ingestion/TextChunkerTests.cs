using DocumentReader.Api.Ingestion;

namespace DocumentReader.Tests.Ingestion;

public sealed class TextChunkerTests
{
    [Fact]
    public void Split_ShortText_ReturnsSingleTrimmedChunk()
    {
        var chunks = TextChunker.Split("  Short text.  ", maxChunkCharacters: 100);

        Assert.Equal(["Short text."], chunks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Split_EmptyOrWhitespace_ReturnsNoChunks(string text)
    {
        Assert.Empty(TextChunker.Split(text, maxChunkCharacters: 100));
    }

    [Fact]
    public void Split_LongText_KeepsEveryChunkWithinLimitAndLosesNoWords()
    {
        var words = Enumerable.Range(1, 500).Select(number => $"word{number}").ToArray();
        var text = string.Join(' ', words);

        var chunks = TextChunker.Split(text, maxChunkCharacters: 120);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 120));
        var rejoinedWords = chunks.SelectMany(chunk => chunk.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(words, rejoinedWords);
    }

    [Fact]
    public void Split_PrefersParagraphBoundary()
    {
        var firstParagraph = new string('a', 60);
        var secondParagraph = new string('b', 60);

        var chunks = TextChunker.Split($"{firstParagraph}\n\n{secondParagraph}", maxChunkCharacters: 100);

        Assert.Equal([firstParagraph, secondParagraph], chunks);
    }

    [Fact]
    public void Split_LongTokenWithoutSpaces_SplitsAtLimit()
    {
        var chunks = TextChunker.Split(new string('x', 250), maxChunkCharacters: 100);

        Assert.Equal([100, 100, 50], chunks.Select(chunk => chunk.Length));
    }
}
