using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace DocumentReader.Api.Ingestion.Pdf;

/// <summary>Text of one PDF page that contained usable text.</summary>
public sealed record PdfPageText(int PageNumber, string Text);

/// <summary>Result of reading a PDF. <see cref="PageCount"/> is the document's total page count when known.</summary>
public sealed record PdfExtractionResult(
    DocumentStatus Status,
    int? PageCount,
    IReadOnlyList<PdfPageText> Pages,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Extracts text page by page with PdfPig. It never executes document content, bounds the
/// pages and characters it reads, and reports scanned, blank, unreadable and truncated
/// content through the status and warnings instead of failing the whole upload.
/// </summary>
public sealed partial class PdfTextExtractor(ILogger<PdfTextExtractor> logger)
{
    /// <summary>Page text beyond this length is cut off, bounding memory for pathological pages.</summary>
    public const int MaxCharactersPerPage = 100_000;

    /// <summary>
    /// Reads up to <paramref name="maxPages"/> pages. Cancelling <paramref name="cancellationToken"/>
    /// stops between pages and returns what was read so far (used for the per-file time limit).
    /// </summary>
    public PdfExtractionResult Extract(byte[] content, int maxPages, CancellationToken cancellationToken)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(content, new ParsingOptions { UseLenientParsing = true });
        }
        catch (PdfDocumentEncryptedException)
        {
            return Failed(DocumentStatus.Corrupt, UploadMessages.PdfPasswordProtected);
        }
        catch (Exception exception)
        {
            logger.LogInformation(exception, "PDF could not be opened.");
            return Failed(DocumentStatus.Corrupt, UploadMessages.PdfDamaged);
        }

        using (document)
        {
            int pageCount;
            try
            {
                pageCount = document.NumberOfPages;
            }
            catch (Exception exception)
            {
                logger.LogInformation(exception, "PDF page count could not be read.");
                return Failed(DocumentStatus.Corrupt, UploadMessages.PdfDamaged);
            }

            return ExtractPages(document, pageCount, maxPages, cancellationToken);
        }
    }

    private PdfExtractionResult ExtractPages(
        PdfDocument document,
        int pageCount,
        int maxPages,
        CancellationToken cancellationToken)
    {
        if (pageCount == 0)
        {
            return new PdfExtractionResult(DocumentStatus.Empty, 0, [], [UploadMessages.PdfNoPages]);
        }

        var pagesToRead = Math.Min(pageCount, maxPages);
        var pages = new List<PdfPageText>();
        var failedPages = new List<int>();
        var imageOnlyPages = new List<int>();
        var truncatedPages = new List<int>();
        var timedOut = false;

        for (var pageNumber = 1; pageNumber <= pagesToRead; pageNumber++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }

            try
            {
                var page = document.GetPage(pageNumber);
                var text = NormalizeText(ContentOrderTextExtractor.GetText(page, addDoubleNewline: true));
                if (text.Length > MaxCharactersPerPage)
                {
                    text = text[..MaxCharactersPerPage];
                    truncatedPages.Add(pageNumber);
                }

                if (ContainsUsableText(text))
                {
                    pages.Add(new PdfPageText(pageNumber, text));
                }
                else if (page.GetImages().Any())
                {
                    // A page with images but no text is most likely a scanned page.
                    imageOnlyPages.Add(pageNumber);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogInformation(exception, "PDF page {PageNumber} could not be read.", pageNumber);
                failedPages.Add(pageNumber);
            }
        }

        var warnings = new List<string>();
        if (failedPages.Count > 0)
        {
            warnings.Add($"Some pages could not be read: {FormatPages(failedPages)}.");
        }

        if (imageOnlyPages.Count > 0 && pages.Count > 0)
        {
            warnings.Add($"Some pages contain only images and may be scanned; their content was not read (OCR is not supported): {FormatPages(imageOnlyPages)}.");
        }

        if (truncatedPages.Count > 0)
        {
            warnings.Add($"Very long page text was cut off at {MaxCharactersPerPage} characters on pages: {FormatPages(truncatedPages)}.");
        }

        if (pagesToRead < pageCount)
        {
            warnings.Add($"Only the first {pagesToRead} of {pageCount} pages were processed (page limit).");
        }

        if (timedOut)
        {
            warnings.Add("Processing stopped because the time limit was reached; later pages were not read.");
        }

        if (pages.Count == 0)
        {
            return new PdfExtractionResult(ClassifyWithoutText(), pageCount, [], WarningsWithoutText());
        }

        var isPartial = failedPages.Count > 0 || imageOnlyPages.Count > 0 || truncatedPages.Count > 0
            || pagesToRead < pageCount || timedOut;
        return new PdfExtractionResult(
            isPartial ? DocumentStatus.PartiallyIndexed : DocumentStatus.Indexed,
            pageCount,
            pages,
            warnings);

        DocumentStatus ClassifyWithoutText()
        {
            if (timedOut || failedPages.Count > 0)
            {
                return DocumentStatus.Corrupt;
            }

            return imageOnlyPages.Count > 0 ? DocumentStatus.NoTextExtracted : DocumentStatus.Empty;
        }

        List<string> WarningsWithoutText()
        {
            if (!timedOut && failedPages.Count > 0)
            {
                warnings.Insert(0, UploadMessages.PdfDamaged);
            }

            if (!timedOut && failedPages.Count == 0)
            {
                warnings.Insert(0, imageOnlyPages.Count > 0
                    ? UploadMessages.PdfScanned
                    : UploadMessages.PdfNoText);
            }

            return warnings;
        }
    }

    private static PdfExtractionResult Failed(DocumentStatus status, string warning) =>
        new(status, null, [], [warning]);

    private static bool ContainsUsableText(string text) => text.Any(char.IsLetterOrDigit);

    private static string FormatPages(List<int> pages)
    {
        const int maxListed = 20;
        var listed = string.Join(", ", pages.Take(maxListed));
        return pages.Count > maxListed ? $"{listed} and {pages.Count - maxListed} more" : listed;
    }

    /// <summary>
    /// Removes control characters, trims each line and collapses runs of spaces and blank lines,
    /// keeping paragraph breaks.
    /// </summary>
    internal static string NormalizeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (character == '\n')
            {
                builder.Append('\n');
            }
            else if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(character);
            }
        }

        var collapsedSpaces = MultipleSpaces().Replace(builder.ToString(), " ");
        var trimmedLines = string.Join('\n', collapsedSpaces.Split('\n').Select(line => line.Trim()));
        return MultipleBlankLines().Replace(trimmedLines, "\n\n").Trim();
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();

    [GeneratedRegex("\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
