using System.Text.Json;
using DocumentReader.Api.Agent.Tools;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>Builds sessions with documents directly, so tools can be tested without HTTP or an LLM.</summary>
public static class TestSessions
{
    private static readonly IOptions<LimitsOptions> Limits = Options.Create(new LimitsOptions());

    public static Session Create(params IngestedDocument[] documents)
    {
        var session = new Session(SessionIdGenerator.Create(), DateTimeOffset.UnixEpoch);
        Assert.True(session.Documents.TryAdd(documents, maxContentDocuments: 100));
        return session;
    }

    /// <summary>A processed workbook, as the upload pipeline would produce it.</summary>
    public static IngestedDocument Workbook(byte[] xlsx, string fileName = "boq.xlsx")
    {
        var extractor = new ExcelDocumentExtractor(
            new XlsxGridReader(Limits), new XlsGridReader(Limits), Limits, NullLogger<ExcelDocumentExtractor>.Instance);
        var result = extractor.Extract(xlsx, FileType.Xlsx, CancellationToken.None);
        var record = new DocumentRecord(
            NewDocumentId(), fileName, FileType.Xlsx, xlsx.Length, result.Status, DateTimeOffset.UnixEpoch,
            null, result.Sheets, result.Tables.Count, result.Chunks.Count, result.Warnings);
        return new IngestedDocument(record, result.Chunks, result.Tables);
    }

    /// <summary>A text document with one chunk per page.</summary>
    public static IngestedDocument TextDocument(string fileName, params string[] pages)
    {
        var chunks = pages
            .Select((text, index) => new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(index + 1), text))
            .ToList();
        var record = new DocumentRecord(
            NewDocumentId(), fileName, FileType.Pdf, 1000, DocumentStatus.Indexed, DateTimeOffset.UnixEpoch,
            pages.Length, null, 0, chunks.Count, []);
        return new IngestedDocument(record, chunks, []);
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Parses a successful tool result; fails the test if the tool returned an error.</summary>
    public static JsonElement Success(ToolResult result)
    {
        Assert.False(result.IsError, result.Content);
        return Json(result.Content);
    }

    /// <summary>Returns the error message of a failed tool result.</summary>
    public static string Error(ToolResult result)
    {
        Assert.True(result.IsError, result.Content);
        return Json(result.Content).GetProperty("error").GetString()!;
    }

    private static string NewDocumentId() => Guid.NewGuid().ToString("N");
}
