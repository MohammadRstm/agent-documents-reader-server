namespace DocumentReader.Api.Ingestion;

/// <summary>Per-sheet summary of an Excel document (docs/api-contract.md).</summary>
public sealed record SheetSummary(string Name, int TableCount, int RowCount);

/// <summary>The DocumentSummary response type from docs/api-contract.md.</summary>
public sealed record DocumentSummary(
    string Id,
    string FileName,
    FileType FileType,
    long SizeBytes,
    DocumentStatus Status,
    DateTime UploadedAt,
    int? PageCount,
    IReadOnlyList<SheetSummary>? Sheets,
    int TableCount,
    int ChunkCount,
    IReadOnlyList<string> Warnings)
{
    public static DocumentSummary From(DocumentRecord document) => new(
        document.Id,
        document.FileName,
        document.FileType,
        document.SizeBytes,
        document.Status,
        document.UploadedAt.UtcDateTime,
        document.PageCount,
        document.Sheets,
        document.TableCount,
        document.ChunkCount,
        document.Warnings);
}

public sealed record DocumentListResponse(IReadOnlyList<DocumentSummary> Documents);
