using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Detects the file type from the file's content ("magic bytes" and, for Office files, the
/// package structure). The file name and the client-supplied content type are never trusted.
/// </summary>
public static class FileTypeDetector
{
    /// <summary>How many leading bytes <see cref="DetectFromHeader"/> needs.</summary>
    public const int HeaderLength = 1024;

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    // OLE compound file, used by legacy .xls (and .doc, .msg, and encrypted Office files).
    private static ReadOnlySpan<byte> CompoundFileSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // Stream names inside compound files, stored as UTF-16LE in the directory.
    private static ReadOnlySpan<byte> WorkbookStreamName => "W\0o\0r\0k\0b\0o\0o\0k\0"u8;

    private static ReadOnlySpan<byte> LegacyBookStreamName => "B\0o\0o\0k\0\0\0"u8;

    private static ReadOnlySpan<byte> EncryptedPackageStreamName => "E\0n\0c\0r\0y\0p\0t\0e\0d\0P\0a\0c\0k\0a\0g\0e\0"u8;

    /// <summary>Detects the type of a complete file.</summary>
    public static FileType Detect(byte[] content)
    {
        if (IsPdf(content))
        {
            return FileType.Pdf;
        }

        if (content.AsSpan().StartsWith(ZipSignature))
        {
            return OpenXmlPackageInspector.DetectSpreadsheetType(content);
        }

        if (IsCompoundFile(content) && !IsEncryptedOfficeDocument(content)
            && (content.AsSpan().IndexOf(WorkbookStreamName) >= 0 || content.AsSpan().IndexOf(LegacyBookStreamName) >= 0))
        {
            return FileType.Xls;
        }

        return FileType.Unknown;
    }

    /// <summary>
    /// Detects the type from the first bytes only, for files too large to read in full.
    /// Only PDF can be recognized this way; Office types need the whole file.
    /// </summary>
    public static FileType DetectFromHeader(ReadOnlySpan<byte> header) =>
        IsPdf(header) ? FileType.Pdf : FileType.Unknown;

    /// <summary>
    /// True for password-protected .xlsx/.docx files, which are stored as a compound file with an
    /// "EncryptedPackage" stream instead of a ZIP package.
    /// </summary>
    public static bool IsEncryptedOfficeDocument(ReadOnlySpan<byte> content) =>
        IsCompoundFile(content) && content.IndexOf(EncryptedPackageStreamName) >= 0;

    private static bool IsPdf(ReadOnlySpan<byte> content)
    {
        // The PDF specification allows the "%PDF-" header anywhere in the first 1024 bytes.
        var searchArea = content.Length > HeaderLength ? content[..HeaderLength] : content;
        return searchArea.IndexOf(PdfSignature) >= 0;
    }

    private static bool IsCompoundFile(ReadOnlySpan<byte> content) => content.StartsWith(CompoundFileSignature);
}
