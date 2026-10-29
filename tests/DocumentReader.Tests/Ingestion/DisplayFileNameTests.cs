using DocumentReader.Api.Ingestion;

namespace DocumentReader.Tests.Ingestion;

public sealed class DisplayFileNameTests
{
    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData(@"C:\Users\someone\report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("  spaced.pdf  ", "spaced.pdf")]
    [InlineData("bad\u0000\u001fname.pdf", "badname.pdf")]
    [InlineData("", "unnamed")]
    [InlineData(null, "unnamed")]
    [InlineData("folder/", "unnamed")]
    public void ToDisplayFileName_StripsPathsAndControlCharacters(string? raw, string expected)
    {
        Assert.Equal(expected, DocumentIngestionService.ToDisplayFileName(raw));
    }

    [Fact]
    public void ToDisplayFileName_TruncatesVeryLongNames()
    {
        var name = DocumentIngestionService.ToDisplayFileName(new string('a', 400) + ".pdf");

        Assert.Equal(255, name.Length);
    }
}
