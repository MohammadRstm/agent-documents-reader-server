using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

/// <summary>
/// Damaged, disguised and unusual files uploaded through the real pipeline. Each must get the right
/// status and a specific, safe message, and must never crash or hang the request.
/// </summary>
public sealed class CorruptedUploadTests(CorruptedUploadTests.CorruptedUploadFactory factory)
    : IClassFixture<CorruptedUploadTests.CorruptedUploadFactory>
{
    private const int MaxFileSizeBytes = 100_000;

    private static readonly Dictionary<string, Func<byte[]>> Files = new()
    {
        ["pdf-truncated"] = () => PdfFixtures.Truncated(),
        ["pdf-header-garbage"] = PdfFixtures.Corrupt,
        ["pdf-broken-startxref"] = PdfFixtures.BrokenStartXref,
        ["pdf-missing-xref"] = PdfFixtures.MissingXref,
        ["pdf-password"] = PdfFixtures.PasswordProtected,
        ["pdf-zero-pages"] = PdfFixtures.ZeroPages,
        ["pdf-scanned"] = () => PdfFixtures.ImageOnly(pageCount: 2),
        ["xlsx-truncated"] = ExcelFixtures.TruncatedXlsx,
        ["xlsx-no-workbook-part"] = ExcelFixtures.XlsxWithoutWorkbookPart,
        ["xlsx-malformed-sheet"] = ExcelFixtures.XlsxWithMalformedSheetXml,
        ["xlsx-encrypted"] = () => ExcelFixtures.CompoundFile("EncryptedPackage"),
        ["xlsx-zip-bomb"] = () => ExcelFixtures.HighlyCompressedPackage(uncompressedPartBytes: 5 * 1024 * 1024),
        ["docx-renamed"] = ExcelFixtures.WordDocument,
        ["xls-truncated"] = XlsFixtures.Truncated,
        ["ole-without-workbook"] = () => ExcelFixtures.CompoundFile("WordDocument"),
        ["ole-garbage"] = ExcelFixtures.CompoundFileGarbage,
        ["zero-bytes"] = () => [],
        ["over-size-limit"] = () => PdfFixtures.PdfHeaderPadded(MaxFileSizeBytes + 1),
        ["text-renamed-pdf"] = () => Encoding.UTF8.GetBytes("Just some notes, not a PDF."),
        ["png"] = ExcelFixtures.Png,
        ["random-bytes"] = () => ExcelFixtures.RandomBytes(),
    };

    [Theory]
    [InlineData("pdf-truncated", "report.pdf", "corrupt", "pdf", UploadMessages.PdfDamaged)]
    [InlineData("pdf-header-garbage", "report.pdf", "corrupt", "pdf", UploadMessages.PdfDamaged)]
    [InlineData("pdf-missing-xref", "report.pdf", "corrupt", "pdf", UploadMessages.PdfDamaged)]
    [InlineData("pdf-password", "secret.pdf", "corrupt", "pdf", UploadMessages.PdfPasswordProtected)]
    [InlineData("pdf-zero-pages", "blank.pdf", "empty", "pdf", UploadMessages.PdfNoPages)]
    [InlineData("pdf-scanned", "scan.pdf", "noTextExtracted", "pdf", UploadMessages.PdfScanned)]
    [InlineData("xlsx-truncated", "boq.xlsx", "corrupt", "xlsx", UploadMessages.WorkbookDamaged)]
    [InlineData("xlsx-no-workbook-part", "boq.xlsx", "corrupt", "xlsx", UploadMessages.WorkbookDamaged)]
    [InlineData("xlsx-malformed-sheet", "boq.xlsx", "corrupt", "xlsx", UploadMessages.WorkbookDamaged)]
    [InlineData("xlsx-encrypted", "locked.xlsx", "corrupt", "unknown", UploadMessages.OfficePasswordProtected)]
    [InlineData("xlsx-zip-bomb", "bomb.xlsx", "tooLarge", "xlsx", "too large to process once uncompressed")]
    [InlineData("docx-renamed", "letter.xlsx", "unsupported", "unknown", UploadMessages.Unsupported)]
    [InlineData("xls-truncated", "old.xls", "corrupt", "xls", UploadMessages.WorkbookDamaged)]
    [InlineData("ole-without-workbook", "memo.xls", "unsupported", "unknown", UploadMessages.Unsupported)]
    [InlineData("ole-garbage", "junk.xls", "unsupported", "unknown", UploadMessages.Unsupported)]
    [InlineData("zero-bytes", "empty.pdf", "empty", "unknown", UploadMessages.Empty)]
    [InlineData("over-size-limit", "huge.pdf", "tooLarge", "pdf", "limit, so it was not processed")]
    [InlineData("text-renamed-pdf", "notes.pdf", "unsupported", "unknown", UploadMessages.Unsupported)]
    [InlineData("png", "photo.pdf", "unsupported", "unknown", UploadMessages.Unsupported)]
    [InlineData("random-bytes", "data.xlsx", "unsupported", "unknown", UploadMessages.Unsupported)]
    public async Task Upload_ProblemFile_GetsSpecificStatusAndSafeMessage(
        string fixture, string fileName, string expectedStatus, string expectedFileType, string expectedWarning)
    {
        var document = await UploadSingleAsync(fileName, Files[fixture]());

        Assert.Equal(expectedStatus, document.GetProperty("status").GetString());
        Assert.Equal(expectedFileType, document.GetProperty("fileType").GetString());
        Assert.Equal(0, document.GetProperty("chunkCount").GetInt32());
        var warnings = document.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToList();
        Assert.Contains(warnings, warning => warning.Contains(expectedWarning, StringComparison.Ordinal));
        AssertNoInternals(warnings);
    }

    [Fact]
    public async Task Upload_PdfWithBrokenStartXref_IsRecoveredAndIndexed()
    {
        var document = await UploadSingleAsync("recoverable.pdf", Files["pdf-broken-startxref"]());

        Assert.Equal("indexed", document.GetProperty("status").GetString());
        Assert.True(document.GetProperty("chunkCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Upload_MixedBatch_GoodFilesAreIndexedAndOrderIsKept()
    {
        var sessionId = await factory.CreateSessionAsync(Client);

        var response = await UploadAsync(
            sessionId,
            ("broken.pdf", PdfFixtures.Truncated()),
            ("good.pdf", PdfFixtures.WithPageTexts("Payment terms: 30 days.")),
            ("broken.xlsx", ExcelFixtures.TruncatedXlsx()),
            ("good.xlsx", ExcelFixtures.BillOfQuantities()),
            ("photo.png", ExcelFixtures.Png()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var documents = await ReadDocumentsAsync(response);
        Assert.Equal(
            ["broken.pdf", "good.pdf", "broken.xlsx", "good.xlsx", "photo.png"],
            documents.Select(document => document.GetProperty("fileName").GetString()));
        Assert.Equal(
            ["corrupt", "indexed", "corrupt", "indexed", "unsupported"],
            documents.Select(document => document.GetProperty("status").GetString()));
    }

    [Fact]
    public void Messages_ForEachProblem_AreDistinct()
    {
        string[] messages =
        [
            UploadMessages.Unsupported, UploadMessages.Damaged, UploadMessages.PdfDamaged, UploadMessages.WorkbookDamaged,
            UploadMessages.PdfPasswordProtected, UploadMessages.OfficePasswordProtected, UploadMessages.Empty,
            UploadMessages.TooLarge(1024), UploadMessages.PdfScanned, UploadMessages.WorkbookTooLargeUncompressed(1024),
            UploadMessages.TimedOut,
        ];

        Assert.Equal(messages.Length, messages.Distinct().Count());
    }

    private HttpClient Client { get; } = factory.CreateClient();

    private static void AssertNoInternals(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            foreach (var forbidden in new[] { "Exception", "System.", "PdfPig", "ClosedXML", "ExcelDataReader", "   at ", ":\\" })
            {
                Assert.DoesNotContain(forbidden, warning);
            }
        }
    }

    private async Task<JsonElement> UploadSingleAsync(string fileName, byte[] content)
    {
        var sessionId = await factory.CreateSessionAsync(Client);
        var response = await UploadAsync(sessionId, (fileName, content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(await ReadDocumentsAsync(response));
    }

    private async Task<HttpResponseMessage> UploadAsync(string sessionId, params (string FileName, byte[] Content)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (fileName, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(part, "files", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = multipart };
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await Client.SendAsync(request);
    }

    private static async Task<List<JsonElement>> ReadDocumentsAsync(HttpResponseMessage response)
    {
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("documents").EnumerateArray().ToList();
    }

    public sealed class CorruptedUploadFactory : ApiFactory
    {
        protected override Dictionary<string, string?> GetSettings()
        {
            var settings = base.GetSettings();
            settings["Limits:MaxFileSizeBytes"] = MaxFileSizeBytes.ToString();
            settings["Limits:MaxDocumentsPerSession"] = "100";
            return settings;
        }
    }
}
