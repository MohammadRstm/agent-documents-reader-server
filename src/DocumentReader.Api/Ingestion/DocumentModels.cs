using System.Text.Json.Serialization;

namespace DocumentReader.Api.Ingestion;

/// <summary>File type detected from the file's content (never from its name).</summary>
public enum FileType
{
    Pdf,
    Xlsx,
    Xlsm,
    Xls,
    Unknown,
}

/// <summary>Outcome of processing one uploaded file. Values match docs/api-contract.md.</summary>
public enum DocumentStatus
{
    /// <summary>Fully extracted.</summary>
    Indexed,

    /// <summary>Some content was extracted, but pages failed or limits truncated it; see warnings.</summary>
    PartiallyIndexed,

    /// <summary>PDF with no usable text (likely scanned).</summary>
    NoTextExtracted,

    /// <summary>Valid file with no content.</summary>
    Empty,

    /// <summary>File content is not a supported type.</summary>
    Unsupported,

    /// <summary>Recognized type but unreadable, encrypted, or not processable within the time limit.</summary>
    Corrupt,

    /// <summary>Over the per-file size limit; not processed.</summary>
    TooLarge,
}

public enum EvidenceKind
{
    TextChunk,
    TableRow,
}

/// <summary>Where a piece of evidence comes from in its document.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PdfPageLocation), "pdfPage")]
public abstract record SourceLocation;

/// <summary>A 1-based PDF page.</summary>
public sealed record PdfPageLocation(int Page) : SourceLocation;

/// <summary>A piece of extracted text with its source location, before it is given an evidence ID.</summary>
public sealed record ExtractedChunk(EvidenceKind Kind, SourceLocation Source, string Text);

/// <summary>Metadata about one uploaded file, as stored in its session.</summary>
public sealed record DocumentRecord(
    string Id,
    string FileName,
    FileType FileType,
    long SizeBytes,
    DocumentStatus Status,
    DateTimeOffset UploadedAt,
    int? PageCount,
    int ChunkCount,
    IReadOnlyList<string> Warnings);

/// <summary>A processed file and its extracted chunks, ready to be added to a session.</summary>
public sealed record IngestedDocument(DocumentRecord Document, IReadOnlyList<ExtractedChunk> Chunks);

/// <summary>An indexed, citable piece of evidence. The ID has the form E&lt;n&gt; and is unique within a session.</summary>
public sealed record EvidenceItem(
    string Id,
    string DocumentId,
    string FileName,
    EvidenceKind Kind,
    SourceLocation Source,
    string Text);
