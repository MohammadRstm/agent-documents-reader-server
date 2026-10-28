using System.Diagnostics;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Ingestion.Pdf;
using DocumentReader.Api.Sessions;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Turns uploaded files into session documents and evidence. Each file is processed
/// independently: a bad file gets its own status and never fails the rest of the batch.
/// Uploaded bytes are kept in memory only while a file is processed; nothing is written to disk
/// by this service.
/// </summary>
public sealed class DocumentIngestionService(
    PdfTextExtractor pdfTextExtractor,
    ExcelDocumentExtractor excelExtractor,
    IOptions<LimitsOptions> limitsOptions,
    TimeProvider timeProvider,
    ILogger<DocumentIngestionService> logger)
{
    /// <summary>Extra time given to a running extraction to stop by itself before it is abandoned.</summary>
    private static readonly TimeSpan HardTimeoutGrace = TimeSpan.FromSeconds(5);

    private const int MaxFileNameLength = 255;

    public async Task<IReadOnlyList<DocumentRecord>> IngestAsync(
        Session session,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        var limits = limitsOptions.Value;

        // The session document limit can only be checked after processing: only files that produce
        // content count toward it, so even a full session still accepts files that turn out to fail.
        var ingested = new List<IngestedDocument>(files.Count);
        foreach (var file in files)
        {
            ingested.Add(await IngestFileAsync(file, limits, cancellationToken));
        }

        // Atomic check-and-add: another upload to the same session may have finished meanwhile.
        if (!session.Documents.TryAdd(ingested, limits.MaxDocumentsPerSession))
        {
            throw SessionLimitExceeded(limits);
        }

        return ingested.Select(item => item.Document).ToList();
    }

    private static ApiException SessionLimitExceeded(LimitsOptions limits) =>
        ApiException.PayloadTooLarge(
            $"A session can hold at most {limits.MaxDocumentsPerSession} readable documents. Start a new session to upload more.");

    private async Task<IngestedDocument> IngestFileAsync(IFormFile file, LimitsOptions limits, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var documentId = Guid.NewGuid().ToString("N");

        var outcome = await ProcessFileAsync(file, limits, cancellationToken);
        var document = new DocumentRecord(
            documentId,
            ToDisplayFileName(file.FileName),
            outcome.FileType,
            file.Length,
            outcome.Status,
            timeProvider.GetUtcNow(),
            outcome.PageCount,
            outcome.Sheets,
            outcome.Tables.Count,
            outcome.Chunks.Count,
            outcome.Warnings);

        // File names and contents are not logged; they may be confidential.
        logger.LogInformation(
            "Processed document {DocumentId}: type {FileType}, status {Status}, {TableCount} tables, {ChunkCount} chunks in {ElapsedMs} ms.",
            documentId,
            document.FileType,
            document.Status,
            document.TableCount,
            document.ChunkCount,
            stopwatch.ElapsedMilliseconds);
        return new IngestedDocument(document, outcome.Chunks, outcome.Tables);
    }

    private async Task<FileOutcome> ProcessFileAsync(IFormFile file, LimitsOptions limits, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            return FileOutcome.Failed(FileType.Unknown, DocumentStatus.Empty, "The file is empty.");
        }

        if (file.Length > limits.MaxFileSizeBytes)
        {
            var header = await ReadHeaderAsync(file, cancellationToken);
            return FileOutcome.Failed(
                FileTypeDetector.DetectFromHeader(header),
                DocumentStatus.TooLarge,
                $"The file is larger than the {SizeFormatter.Format(limits.MaxFileSizeBytes)} limit and was not processed.");
        }

        var content = await ReadAllAsync(file, cancellationToken);
        var fileType = FileTypeDetector.Detect(content);
        switch (fileType)
        {
            case FileType.Pdf:
                var pdf = await RunWithTimeLimitAsync(
                    token => pdfTextExtractor.Extract(content, limits.MaxPdfPages, token),
                    limits,
                    cancellationToken);
                return pdf is null
                    ? FileOutcome.TimedOut(FileType.Pdf)
                    : new FileOutcome(FileType.Pdf, pdf.Status, pdf.PageCount, null, [], ToChunks(pdf.Pages), pdf.Warnings);

            case FileType.Xlsx or FileType.Xlsm or FileType.Xls:
                var excel = await RunWithTimeLimitAsync(
                    token => excelExtractor.Extract(content, fileType, token),
                    limits,
                    cancellationToken);
                return excel is null
                    ? FileOutcome.TimedOut(fileType)
                    : new FileOutcome(fileType, excel.Status, null, excel.Sheets.Count > 0 ? excel.Sheets : null, excel.Tables, excel.Chunks, excel.Warnings);

            default:
                return FileTypeDetector.IsEncryptedOfficeDocument(content)
                    ? FileOutcome.Failed(FileType.Unknown, DocumentStatus.Corrupt, "The file is a password-protected Office document and cannot be read.")
                    : FileOutcome.Failed(FileType.Unknown, DocumentStatus.Unsupported, UnsupportedTypeWarning);
        }
    }

    /// <summary>
    /// Runs a synchronous extractor off the request thread with the per-file time limit. Extractors
    /// stop cooperatively when the token is cancelled and return what they read so far. If an
    /// extractor is stuck inside a library call, it is abandoned after a short grace period and
    /// null is returned.
    /// </summary>
    private async Task<T?> RunWithTimeLimitAsync<T>(
        Func<CancellationToken, T> extract,
        LimitsOptions limits,
        CancellationToken cancellationToken)
        where T : class
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limits.ExtractionTimeout);
        var extractionToken = timeoutSource.Token;

        var extraction = Task.Run(() => extract(extractionToken), CancellationToken.None);

        T result;
        try
        {
            result = await extraction.WaitAsync(limits.ExtractionTimeout + HardTimeoutGrace, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Extraction did not stop within the time limit and was abandoned.");
            return null;
        }

        // Extractors treat cancellation as "time is up"; if the client went away, stop here instead.
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private const string UnsupportedTypeWarning =
        "This file type is not supported. Supported types: PDF and Excel (.xlsx, .xlsm, .xls).";

    /// <summary>The extraction outcome of one file, independent of its type.</summary>
    private sealed record FileOutcome(
        FileType FileType,
        DocumentStatus Status,
        int? PageCount,
        IReadOnlyList<SheetSummary>? Sheets,
        IReadOnlyList<DetectedTable> Tables,
        IReadOnlyList<ExtractedChunk> Chunks,
        IReadOnlyList<string> Warnings)
    {
        public static FileOutcome Failed(FileType fileType, DocumentStatus status, string warning) =>
            new(fileType, status, null, null, [], [], [warning]);

        public static FileOutcome TimedOut(FileType fileType) =>
            Failed(fileType, DocumentStatus.Corrupt, "The file could not be processed within the time limit.");
    }

    private static List<ExtractedChunk> ToChunks(IReadOnlyList<PdfPageText> pages) =>
        pages
            .SelectMany(page => TextChunker.Split(page.Text)
                .Select(text => new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(page.PageNumber), text)))
            .ToList();

    private static async Task<byte[]> ReadAllAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var content = new byte[file.Length];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(content, cancellationToken);
        return content;
    }

    private static async Task<byte[]> ReadHeaderAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var header = new byte[Math.Min(FileTypeDetector.HeaderLength, file.Length)];
        await using var stream = file.OpenReadStream();
        await stream.ReadExactlyAsync(header, cancellationToken);
        return header;
    }

    /// <summary>
    /// The original name is for display only and is never used as a path. Directory parts and
    /// control characters are removed and the length is bounded.
    /// </summary>
    internal static string ToDisplayFileName(string? rawFileName)
    {
        var name = Path.GetFileName((rawFileName ?? string.Empty).Replace('\\', '/'));
        name = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0)
        {
            return "unnamed";
        }

        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] : name;
    }
}
