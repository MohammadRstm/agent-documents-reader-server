using DocumentReader.Api.Configuration;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Ingestion.Pdf;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Ingestion;

/// <summary>The extraction outcome of one file, independent of its type.</summary>
public sealed record ExtractionResult(
    DocumentStatus Status,
    int? PageCount,
    IReadOnlyList<SheetSummary>? Sheets,
    IReadOnlyList<DetectedTable> Tables,
    IReadOnlyList<ExtractedChunk> Chunks,
    IReadOnlyList<string> Warnings)
{
    public static ExtractionResult Failed(DocumentStatus status, string warning) => new(status, null, null, [], [], [warning]);
}

/// <summary>
/// Extracts the content of a file whose type has already been detected. Synchronous and CPU-bound;
/// it must stop at the next safe point when <c>cancellationToken</c> is cancelled (the time limit)
/// and return what it has. Replaceable in tests.
/// </summary>
public interface IDocumentExtractor
{
    ExtractionResult Extract(byte[] content, FileType fileType, CancellationToken cancellationToken);
}

/// <summary>Routes PDFs to <see cref="PdfTextExtractor"/> and workbooks to <see cref="ExcelDocumentExtractor"/>.</summary>
public sealed class DocumentExtractor(
    PdfTextExtractor pdfTextExtractor,
    ExcelDocumentExtractor excelExtractor,
    IOptions<LimitsOptions> limitsOptions) : IDocumentExtractor
{
    public ExtractionResult Extract(byte[] content, FileType fileType, CancellationToken cancellationToken)
    {
        switch (fileType)
        {
            case FileType.Pdf:
                var pdf = pdfTextExtractor.Extract(content, limitsOptions.Value.MaxPdfPages, cancellationToken);
                return new ExtractionResult(pdf.Status, pdf.PageCount, null, [], ToChunks(pdf.Pages), pdf.Warnings);

            case FileType.Xlsx or FileType.Xlsm or FileType.Xls:
                var excel = excelExtractor.Extract(content, fileType, cancellationToken);
                return new ExtractionResult(
                    excel.Status, null, excel.Sheets.Count > 0 ? excel.Sheets : null, excel.Tables, excel.Chunks, excel.Warnings);

            default:
                throw new ArgumentOutOfRangeException(nameof(fileType), fileType, "Only PDF and Excel files can be extracted.");
        }
    }

    private static List<ExtractedChunk> ToChunks(IReadOnlyList<PdfPageText> pages) =>
        pages
            .SelectMany(page => TextChunker.Split(page.Text)
                .Select(text => new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(page.PageNumber), text)))
            .ToList();
}
