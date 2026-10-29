using System.Text;
using DocumentReader.Api.Ingestion;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Ingestion;

public sealed class FileTypeDetectorTests
{
    [Fact]
    public void Detect_PdfHeaderAtStart_ReturnsPdf()
    {
        Assert.Equal(FileType.Pdf, FileTypeDetector.Detect("%PDF-1.7\n..."u8.ToArray()));
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
    [InlineData("PK\u0003\u0004 not really a zip archive")]
    public void Detect_UnrecognizedContent_ReturnsUnknown(string content)
    {
        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(Encoding.Latin1.GetBytes(content)));
    }

    [Fact]
    public void Detect_XlsxWorkbook_ReturnsXlsx()
    {
        var workbook = ExcelFixtures.Xlsx(book => book.AddWorksheet("Data").Cell("A1").Value = "Hello");

        Assert.Equal(FileType.Xlsx, FileTypeDetector.Detect(workbook));
    }

    // Workbook content type declared as the Default for ".xml" (System.IO.Packaging / ClosedXML style).
    [Theory]
    [InlineData("/xl/workbook.xml")]
    [InlineData("xl/workbook.xml")]
    [InlineData("/XL/Workbook.xml")]
    public void Detect_WorkbookTypeDeclaredAsDefault_ReturnsXlsx(string target)
    {
        var package = ExcelFixtures.PackageWithRelationships(
            $"<Default Extension=\"xml\" ContentType=\"{ExcelFixtures.XlsxWorkbookContentType}\"/>",
            target);

        Assert.Equal(FileType.Xlsx, FileTypeDetector.Detect(package));
    }

    [Fact]
    public void Detect_OverrideForWorkbookPart_WinsOverDefault()
    {
        var package = ExcelFixtures.PackageWithRelationships(
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{ExcelFixtures.XlsmWorkbookContentType}\"/>",
            "xl/workbook.xml");

        Assert.Equal(FileType.Xlsm, FileTypeDetector.Detect(package));
    }

    [Fact]
    public void Detect_OfficeDocumentThatIsNotAWorkbook_ReturnsUnknown()
    {
        var package = ExcelFixtures.PackageWithRelationships(
            "<Default Extension=\"xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>",
            "word/document.xml");

        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(package));
    }

    [Fact]
    public void Detect_PackageWithMacroEnabledWorkbookType_ReturnsXlsm()
    {
        var package = ExcelFixtures.Package(ExcelFixtures.XlsmWorkbookContentType, ("xl/workbook.xml", "<workbook/>"));

        Assert.Equal(FileType.Xlsm, FileTypeDetector.Detect(package));
    }

    [Fact]
    public void Detect_WordDocumentPackage_ReturnsUnknown()
    {
        var package = ExcelFixtures.Package(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            ("word/document.xml", "<document/>"));

        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(package));
    }

    [Fact]
    public void Detect_ZipWithoutContentTypes_ReturnsUnknown()
    {
        var zip = ExcelFixtures.Zip(("readme.txt", "plain zip"));

        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(zip));
    }

    [Fact]
    public void Detect_CompoundFileWithWorkbookStream_ReturnsXls()
    {
        Assert.Equal(FileType.Xls, FileTypeDetector.Detect(ExcelFixtures.CompoundFile("Workbook")));
    }

    [Fact]
    public void Detect_CompoundFileWithoutWorkbookStream_ReturnsUnknown()
    {
        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(ExcelFixtures.CompoundFile("WordDocument")));
    }

    [Fact]
    public void Detect_EncryptedOfficePackage_IsUnknownAndReportedAsEncrypted()
    {
        var content = ExcelFixtures.CompoundFile("EncryptedPackage");

        Assert.Equal(FileType.Unknown, FileTypeDetector.Detect(content));
        Assert.True(FileTypeDetector.IsEncryptedOfficeDocument(content));
    }

    [Fact]
    public void DetectFromHeader_RecognizesOnlyPdf()
    {
        Assert.Equal(FileType.Pdf, FileTypeDetector.DetectFromHeader("%PDF-1.7"u8));
        Assert.Equal(FileType.Unknown, FileTypeDetector.DetectFromHeader("PK\u0003\u0004"u8));
    }
}
