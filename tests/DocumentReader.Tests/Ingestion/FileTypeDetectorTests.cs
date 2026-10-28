using System.Text;
using DocumentReader.Api.Ingestion;

namespace DocumentReader.Tests.Ingestion;

public sealed class FileTypeDetectorTests
{
    [Fact]
    public void Detect_PdfHeaderAtStart_ReturnsPdf()
    {
        Assert.Equal(FileType.Pdf, FileTypeDetector.Detect("%PDF-1.7\n..."u8));
    }

    [Fact]
    public void Detect_PdfHeaderWithinFirst1024Bytes_ReturnsPdf()
    {
        var content = Encoding.ASCII.GetBytes(new string(' ', 500) + "%PDF-1.4\n");

        Assert.Equal(FileType.Pdf, FileTypeDetector.Detect(content));
    }

    [Fact]
    public void Detect_PdfHeaderAfterFirst1024Bytes_ReturnsUnknown()
    {
        var content = Encoding.ASCII.GetBytes(new string(' ', 1100) + "%PDF-1.4\n");

        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(content));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Just some text that claims to be a PDF")]
    [InlineData("PK\u0003\u0004 zip archive")]
    public void Detect_NonPdfContent_ReturnsUnknown(string content)
    {
        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(Encoding.Latin1.GetBytes(content)));
    }
}
