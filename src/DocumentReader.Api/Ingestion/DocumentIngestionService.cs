using System.Diagnostics;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
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
        EnsureWithinSessionLimit(session, files.Count, limits);

        var ingested = new List<IngestedDocument>(files.Count);
        foreach (var file in files)
        {
            ingested.Add(await IngestFileAsync(file, limits, cancellationToken));
        }

        // Checked again atomically: another upload to the same session may have finished meanwhile.
        if (!session.Documents.TryAdd(ingested, limits.MaxDocumentsPerSession))
        {
            throw SessionLimitExceeded(limits);
        }

        return ingested.Select(item => item.Document).ToList();
    }

    private static void EnsureWithinSessionLimit(Session session, int newDocumentCount, LimitsOptions limits)
    {
        if (session.Documents.Count + newDocumentCount > limits.MaxDocumentsPerSession)
        {
            throw SessionLimitExceeded(limits);
        }
    }

    private static ApiException SessionLimitExceeded(LimitsOptions limits) =>
        ApiException.PayloadTooLarge(
            $"A session can hold at most {limits.MaxDocumentsPerSession} documents. Start a new session to upload more.");

    private async Task<IngestedDocument> IngestFileAsync(IFormFile file, LimitsOptions limits, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var documentId = Guid.NewGuid().ToString("N");
        var fileName = ToDisplayFileName(file.FileName);
        var uploadedAt = timeProvider.GetUtcNow();

        IngestedDocument Result(FileType fileType, DocumentStatus status, int? pageCount, IReadOnlyList<ExtractedChunk> chunks, IReadOnlyList<string> warnings) =>
            new(new DocumentRecord(documentId, fileName, fileType, file.Length, status, uploadedAt, pageCount, chunks.Count, warnings), chunks);

        IngestedDocument result;
        if (file.Length == 0)
        {
            result = Result(FileType.Unknown, DocumentStatus.Empty, null, [], ["The file is empty."]);
        }
        else if (file.Length > limits.MaxFileSizeBytes)
        {
            var header = await ReadHeaderAsync(file, cancellationToken);
            result = Result(
                FileTypeDetector.Detect(header),
                DocumentStatus.TooLarge,
                null,
                [],
                [$"The file is larger than the {SizeFormatter.Format(limits.MaxFileSizeBytes)} limit and was not processed."]);
        }
        else
        {
            var content = await ReadAllAsync(file, cancellationToken);
            var fileType = FileTypeDetector.Detect(content);
            if (fileType == FileType.Pdf)
            {
                var extraction = await ExtractPdfAsync(content, limits, cancellationToken);
                result = Result(FileType.Pdf, extraction.Status, extraction.PageCount, ToChunks(extraction.Pages), extraction.Warnings);
            }
            else
            {
                result = Result(fileType, DocumentStatus.Unsupported, null, [], ["This file type is not supported. Supported type: PDF."]);
            }
        }

        // File names and contents are not logged; they may be confidential.
        logger.LogInformation(
            "Processed document {DocumentId}: type {FileType}, status {Status}, {ChunkCount} chunks in {ElapsedMs} ms.",
            documentId,
            result.Document.FileType,
            result.Document.Status,
            result.Document.ChunkCount,
            stopwatch.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Runs the synchronous PDF extraction off the request thread with a time limit. The extractor
    /// stops cooperatively between pages when the limit is reached; if a single page hangs, the
    /// extraction is abandoned after a short grace period and the file is reported as unreadable.
    /// </summary>
    private async Task<PdfExtractionResult> ExtractPdfAsync(byte[] content, LimitsOptions limits, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(limits.ExtractionTimeout);
        var extractionToken = timeoutSource.Token;

        var extraction = Task.Run(
            () => pdfTextExtractor.Extract(content, limits.MaxPdfPages, extractionToken),
            CancellationToken.None);

        PdfExtractionResult result;
        try
        {
            result = await extraction.WaitAsync(limits.ExtractionTimeout + HardTimeoutGrace, cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("PDF extraction did not stop within the time limit and was abandoned.");
            return new PdfExtractionResult(
                DocumentStatus.Corrupt,
                null,
                [],
                ["The PDF could not be processed within the time limit."]);
        }

        // The extractor treats cancellation as "time is up"; if the client went away, stop here instead.
        cancellationToken.ThrowIfCancellationRequested();
        return result;
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
