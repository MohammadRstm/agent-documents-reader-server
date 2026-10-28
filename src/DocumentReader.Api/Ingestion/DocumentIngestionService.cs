using System.Diagnostics;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Sessions;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Turns uploaded files into session documents and evidence.
/// <list type="bullet">
/// <item>Files of one request are parsed concurrently, bounded server-wide by <see cref="ExtractionLimiter"/>;
/// results keep the upload order.</item>
/// <item>Each file is independent: it has its own time limit, and a bad, failing or hanging file gets its
/// own status and never fails or blocks the others.</item>
/// <item>Uploaded bytes are kept in memory only while a file is processed; nothing is written to disk by
/// this service.</item>
/// </list>
/// </summary>
public sealed class DocumentIngestionService(
    IDocumentExtractor extractor,
    ExtractionLimiter extractionLimiter,
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

        // The files of one request share one buffered request body, so their bytes are read one at a time;
        // parsing then runs concurrently.
        using var bodyReadLock = new SemaphoreSlim(1, 1);
        var ingested = await Task.WhenAll(files.Select(file => IngestFileAsync(file, limits, bodyReadLock, cancellationToken)));

        // The session document limit can only be checked after processing: only files that produce content
        // count toward it. The check-and-add is atomic, so parallel uploads to one session cannot exceed it.
        if (!session.Documents.TryAdd(
                ingested, limits.MaxDocumentsPerSession, limits.MaxFailedDocumentsPerSession, out var notStoredIds))
        {
            throw SessionLimitExceeded(limits);
        }

        // Failed files beyond the cap are still reported to the caller, with a note that they were not kept.
        var notStoredWarning =
            $"This file was not kept in the session because it already holds the maximum of {limits.MaxFailedDocumentsPerSession} files that could not be read.";
        return ingested
            .Select(item => notStoredIds.Contains(item.Document.Id)
                ? item.Document with { Warnings = [.. item.Document.Warnings, notStoredWarning] }
                : item.Document)
            .ToList();
    }

    private static ApiException SessionLimitExceeded(LimitsOptions limits) =>
        ApiException.PayloadTooLarge(
            $"A session can hold at most {limits.MaxDocumentsPerSession} readable documents. Start a new session to upload more.");

    private async Task<IngestedDocument> IngestFileAsync(
        IFormFile file,
        LimitsOptions limits,
        SemaphoreSlim bodyReadLock,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var documentId = Guid.NewGuid().ToString("N");

        var (fileType, outcome) = await ProcessFileAsync(file, limits, bodyReadLock, cancellationToken);
        var document = new DocumentRecord(
            documentId,
            ToDisplayFileName(file.FileName),
            fileType,
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

    /// <summary>Never throws except for cancellation: any unexpected failure becomes a per-file status.</summary>
    private async Task<(FileType FileType, ExtractionResult Outcome)> ProcessFileAsync(
        IFormFile file,
        LimitsOptions limits,
        SemaphoreSlim bodyReadLock,
        CancellationToken cancellationToken)
    {
        var fileType = FileType.Unknown;
        try
        {
            if (file.Length == 0)
            {
                return (fileType, ExtractionResult.Failed(DocumentStatus.Empty, UploadMessages.Empty));
            }

            if (file.Length > limits.MaxFileSizeBytes)
            {
                var header = await ReadAsync(file, FileTypeDetector.HeaderLength, bodyReadLock, cancellationToken);
                return (FileTypeDetector.DetectFromHeader(header),
                    ExtractionResult.Failed(DocumentStatus.TooLarge, UploadMessages.TooLarge(limits.MaxFileSizeBytes)));
            }

            var content = await ReadAsync(file, file.Length, bodyReadLock, cancellationToken);
            fileType = FileTypeDetector.Detect(content);
            if (fileType == FileType.Unknown)
            {
                return (fileType, UnrecognizedContent(content, file.FileName));
            }

            var detectedType = fileType;
            var result = await RunWithTimeLimitAsync(token => extractor.Extract(content, detectedType, token), limits, cancellationToken);
            return (fileType, result ?? ExtractionResult.Failed(DocumentStatus.Corrupt, UploadMessages.TimedOut));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Unexpected failure while processing an uploaded file.");
            return (fileType, ExtractionResult.Failed(DocumentStatus.Corrupt, UploadMessages.Damaged));
        }
    }

    /// <summary>
    /// Outcome for content that is not a recognizable PDF or Excel file. The file name is used ONLY to
    /// choose the status and message: a file named .pdf/.xlsx/.xlsm/.xls is most likely a damaged or
    /// renamed file of that kind, which deserves a more specific message than "not supported". The name
    /// never selects a parser and never bypasses content-based detection; <c>fileType</c> stays what the
    /// content says.
    /// </summary>
    private static ExtractionResult UnrecognizedContent(byte[] content, string? fileName)
    {
        if (FileTypeDetector.IsEncryptedOfficeDocument(content))
        {
            return ExtractionResult.Failed(DocumentStatus.Corrupt, UploadMessages.OfficePasswordProtected);
        }

        return Path.GetExtension(ToDisplayFileName(fileName)).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" or ".xls" => ExtractionResult.Failed(DocumentStatus.Corrupt, UploadMessages.NamedLikeWorkbookButUnreadable),
            ".pdf" => ExtractionResult.Failed(DocumentStatus.Corrupt, UploadMessages.NamedLikePdfButUnreadable),
            _ => ExtractionResult.Failed(DocumentStatus.Unsupported, UploadMessages.Unsupported),
        };
    }

    /// <summary>
    /// Runs a synchronous extractor under the server-wide limiter with the per-file time limit (which
    /// starts once a slot is free). Extractors stop cooperatively when the token is cancelled and return
    /// what they read so far. If one is stuck inside a library call it is abandoned after a short grace
    /// period and null is returned; its slot stays taken until it really ends.
    /// </summary>
    private async Task<ExtractionResult?> RunWithTimeLimitAsync(
        Func<CancellationToken, ExtractionResult> extract,
        LimitsOptions limits,
        CancellationToken cancellationToken)
    {
        // Disposed when the extraction itself ends, not when this method returns: an abandoned
        // extraction may still read its token afterwards.
        var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ExtractionResult> extraction;
        try
        {
            extraction = await extractionLimiter.StartAsync(
                () =>
                {
                    timeoutSource.CancelAfter(limits.ExtractionTimeout);
                    return extract(timeoutSource.Token);
                },
                cancellationToken);
        }
        catch
        {
            timeoutSource.Dispose();
            throw;
        }

        _ = extraction.ContinueWith(
            static (_, source) => ((CancellationTokenSource)source!).Dispose(),
            timeoutSource,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        ExtractionResult result;
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

    private static async Task<byte[]> ReadAsync(IFormFile file, long length, SemaphoreSlim bodyReadLock, CancellationToken cancellationToken)
    {
        var content = new byte[Math.Min(length, file.Length)];
        await bodyReadLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = file.OpenReadStream();
            await stream.ReadExactlyAsync(content, cancellationToken);
        }
        finally
        {
            bodyReadLock.Release();
        }

        return content;
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
