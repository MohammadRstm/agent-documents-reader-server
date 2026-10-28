using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

public sealed class DocumentsEndpointTests(DocumentsEndpointTests.SmallLimitsFactory factory)
    : IClassFixture<DocumentsEndpointTests.SmallLimitsFactory>
{
    private const int MaxFileSizeBytes = 50_000;
    private const int MaxUploadRequestBytes = 200_000;
    private const int MaxFilesPerUpload = 3;
    private const int MaxDocumentsPerSession = 5;

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Upload_TextPdf_ReturnsIndexedSummary()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var pdf = PdfFixtures.WithPages(["Contract A", "Payment terms are 30 days."], ["Delivery in March."]);

        var response = await UploadAsync(sessionId, ("contract-a.pdf", pdf));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.False(string.IsNullOrWhiteSpace(document.GetProperty("id").GetString()));
        Assert.Equal("contract-a.pdf", document.GetProperty("fileName").GetString());
        Assert.Equal("pdf", document.GetProperty("fileType").GetString());
        Assert.Equal(pdf.Length, document.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("indexed", document.GetProperty("status").GetString());
        Assert.EndsWith("Z", document.GetProperty("uploadedAt").GetString());
        Assert.Equal(2, document.GetProperty("pageCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("sheets").ValueKind);
        Assert.Equal(0, document.GetProperty("tableCount").GetInt32());
        Assert.Equal(2, document.GetProperty("chunkCount").GetInt32());
        Assert.Equal(0, document.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task Upload_MixedBatch_ReportsEachFileInOrderWithoutFailingTheBatch()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(
            sessionId,
            ("good.pdf", PdfFixtures.WithPageTexts("Valid content")),
            ("broken.pdf", PdfFixtures.Corrupt()),
            ("notes.pdf", Encoding.UTF8.GetBytes("plain text pretending to be a PDF")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var documents = await ReadDocumentsAsync(response);
        Assert.Equal(["good.pdf", "broken.pdf", "notes.pdf"], documents.Select(d => d.GetProperty("fileName").GetString()));
        Assert.Equal(["indexed", "corrupt", "unsupported"], documents.Select(d => d.GetProperty("status").GetString()));
        Assert.Equal("unknown", documents[2].GetProperty("fileType").GetString());
        Assert.All(documents.Skip(1), document => Assert.True(document.GetProperty("warnings").GetArrayLength() > 0));
    }

    [Fact]
    public async Task Upload_PdfContentWithOtherExtension_IsDetectedFromContent()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, ("renamed.txt", PdfFixtures.WithPageTexts("Hidden PDF")));

        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.Equal("pdf", document.GetProperty("fileType").GetString());
        Assert.Equal("indexed", document.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_Workbook_ReturnsExcelSummaryWithSheetsAndTables()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, ("boq.xlsx", ExcelFixtures.BillOfQuantities()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.Equal("xlsx", document.GetProperty("fileType").GetString());
        Assert.Equal("indexed", document.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.GetProperty("pageCount").ValueKind);
        Assert.Equal(2, document.GetProperty("tableCount").GetInt32());
        var sheet = Assert.Single(document.GetProperty("sheets").EnumerateArray());
        Assert.Equal("BOQ", sheet.GetProperty("name").GetString());
        Assert.Equal(2, sheet.GetProperty("tableCount").GetInt32());
        Assert.Equal(16, sheet.GetProperty("rowCount").GetInt32());
        // 9 BOQ rows + 2 milestone rows + 1 text chunk for the title and note.
        Assert.Equal(12, document.GetProperty("chunkCount").GetInt32());
    }

    [Fact]
    public async Task Upload_PasswordProtectedOfficeFile_ReturnsCorrupt()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, ("locked.xlsx", ExcelFixtures.CompoundFile("EncryptedPackage")));

        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.Equal("corrupt", document.GetProperty("status").GetString());
        Assert.Contains("password", document.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task Upload_ScannedPdf_ReturnsNoTextExtracted()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, ("scan.pdf", PdfFixtures.ImageOnly(pageCount: 1)));

        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.Equal("noTextExtracted", document.GetProperty("status").GetString());
        Assert.Equal(0, document.GetProperty("chunkCount").GetInt32());
    }

    [Fact]
    public async Task Upload_EmptyFile_ReturnsEmptyStatus()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, ("empty.pdf", Array.Empty<byte>()));

        var document = Assert.Single(await ReadDocumentsAsync(response));
        Assert.Equal("empty", document.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_FileOverPerFileLimit_ReturnsTooLargeStatus()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await UploadAsync(
            sessionId,
            ("huge.pdf", PdfFixtures.PdfHeaderPadded(MaxFileSizeBytes + 1)),
            ("small.pdf", PdfFixtures.WithPageTexts("Small file")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var documents = await ReadDocumentsAsync(response);
        Assert.Equal("tooLarge", documents[0].GetProperty("status").GetString());
        Assert.Equal("pdf", documents[0].GetProperty("fileType").GetString());
        Assert.Equal("indexed", documents[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_TooManyFiles_Returns413AndStoresNothing()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var files = Enumerable.Range(1, MaxFilesPerUpload + 1)
            .Select(i => ($"file{i}.pdf", PdfFixtures.WithPageTexts($"File {i}")))
            .ToArray();

        var response = await UploadAsync(sessionId, files);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        Assert.Empty(await ListDocumentsAsync(sessionId));
    }

    [Fact]
    public async Task Upload_RequestOverTotalSizeLimit_Returns413()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var largeFile = PdfFixtures.PdfHeaderPadded(MaxUploadRequestBytes / 2);

        var response = await UploadAsync(sessionId, ("a.pdf", largeFile), ("b.pdf", largeFile), ("c.pdf", largeFile));

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        Assert.Empty(await ListDocumentsAsync(sessionId));
    }

    [Fact]
    public async Task Upload_OverSessionDocumentLimit_Returns413AndKeepsExistingDocuments()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var pdf = PdfFixtures.WithPageTexts("Content");
        var firstBatch = await UploadAsync(sessionId, ("1.pdf", pdf), ("2.pdf", pdf), ("3.pdf", pdf));
        Assert.Equal(HttpStatusCode.OK, firstBatch.StatusCode);

        var secondBatch = await UploadAsync(sessionId, ("4.pdf", pdf), ("5.pdf", pdf), ("6.pdf", pdf));

        await ErrorAssertions.AssertErrorAsync(secondBatch, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        Assert.Equal(3, (await ListDocumentsAsync(sessionId)).Count);
    }

    [Fact]
    public async Task Upload_FailedFiles_DoNotCountTowardSessionDocumentLimit()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var pdf = PdfFixtures.WithPageTexts("Content");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(sessionId, ("1.pdf", pdf), ("2.pdf", pdf), ("3.pdf", pdf))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(sessionId, ("4.pdf", pdf), ("5.pdf", pdf))).StatusCode);

        // The session now holds the maximum of 5 readable documents; failed files are still accepted.
        var failedUpload = await UploadAsync(sessionId, ("broken.pdf", PdfFixtures.Corrupt()), ("notes.pdf", Encoding.UTF8.GetBytes("text")));
        Assert.Equal(HttpStatusCode.OK, failedUpload.StatusCode);
        Assert.Equal(["corrupt", "unsupported"], (await ReadDocumentsAsync(failedUpload)).Select(d => d.GetProperty("status").GetString()));

        var readableUpload = await UploadAsync(sessionId, ("6.pdf", pdf));
        await ErrorAssertions.AssertErrorAsync(readableUpload, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        Assert.Equal(7, (await ListDocumentsAsync(sessionId)).Count);
    }

    [Fact]
    public async Task Upload_WithoutFilesField_Returns400ValidationFailed()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var content = new MultipartFormDataContent { { new StringContent("hello"), "note" } };

        var response = await SendAsync(sessionId, HttpMethod.Post, content);

        var root = await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(root.GetProperty("details").TryGetProperty("files", out _));
    }

    [Fact]
    public async Task Upload_NotMultipart_Returns415UnsupportedMediaType()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var content = new StringContent("""{ "files": [] }""", Encoding.UTF8, "application/json");

        var response = await SendAsync(sessionId, HttpMethod.Post, content);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.UnsupportedMediaType);
    }

    [Fact]
    public async Task Upload_WithoutSession_Returns400SessionRequired()
    {
        using var content = CreateMultipart(("a.pdf", PdfFixtures.WithPageTexts("Content")));

        var response = await _client.PostAsync("/api/documents", content);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    [Fact]
    public async Task List_ReturnsDocumentsInUploadOrder()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        await UploadAsync(sessionId, ("first.pdf", PdfFixtures.WithPageTexts("One")));
        await UploadAsync(sessionId, ("second.pdf", PdfFixtures.WithPageTexts("Two")), ("third.pdf", PdfFixtures.Corrupt()));

        var documents = await ListDocumentsAsync(sessionId);

        Assert.Equal(["first.pdf", "second.pdf", "third.pdf"], documents.Select(d => d.GetProperty("fileName").GetString()));
    }

    [Fact]
    public async Task List_DoesNotShowDocumentsFromAnotherSession()
    {
        var owner = await factory.CreateSessionAsync(_client);
        var other = await factory.CreateSessionAsync(_client);
        await UploadAsync(owner, ("private.pdf", PdfFixtures.WithPageTexts("Confidential")));

        Assert.Single(await ListDocumentsAsync(owner));
        Assert.Empty(await ListDocumentsAsync(other));
    }

    [Fact]
    public async Task List_WithoutSession_Returns400SessionRequired()
    {
        var response = await _client.GetAsync("/api/documents");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    private async Task<HttpResponseMessage> UploadAsync(string sessionId, params (string FileName, byte[] Content)[] files) =>
        await SendAsync(sessionId, HttpMethod.Post, CreateMultipart(files));

    private async Task<List<JsonElement>> ListDocumentsAsync(string sessionId)
    {
        var response = await SendAsync(sessionId, HttpMethod.Get, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadDocumentsAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(string sessionId, HttpMethod method, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, "/api/documents") { Content = content };
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request);
    }

    private static MultipartFormDataContent CreateMultipart(params (string FileName, byte[] Content)[] files)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (fileName, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(part, "files", fileName);
        }

        return multipart;
    }

    private static async Task<List<JsonElement>> ReadDocumentsAsync(HttpResponseMessage response)
    {
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("documents").EnumerateArray().ToList();
    }

    public sealed class SmallLimitsFactory : ApiFactory
    {
        protected override Dictionary<string, string?> GetSettings()
        {
            var settings = base.GetSettings();
            settings["Limits:MaxFileSizeBytes"] = MaxFileSizeBytes.ToString();
            settings["Limits:MaxUploadRequestBytes"] = MaxUploadRequestBytes.ToString();
            settings["Limits:MaxFilesPerUpload"] = MaxFilesPerUpload.ToString();
            settings["Limits:MaxDocumentsPerSession"] = MaxDocumentsPerSession.ToString();
            return settings;
        }
    }
}
