namespace DocumentReader.Api.Ingestion;

/// <summary>
/// Detects the file type from its leading bytes ("magic bytes"). The file name and the
/// client-supplied content type are never trusted for this.
/// </summary>
public static class FileTypeDetector
{
    /// <summary>How many leading bytes callers should pass in.</summary>
    public const int HeaderLength = 1024;

    private static ReadOnlySpan<byte> PdfSignature => "%PDF-"u8;

    public static FileType Detect(ReadOnlySpan<byte> header)
    {
        // The PDF specification allows the "%PDF-" header anywhere in the first 1024 bytes.
        var searchArea = header.Length > HeaderLength ? header[..HeaderLength] : header;
        if (searchArea.IndexOf(PdfSignature) >= 0)
        {
            return FileType.Pdf;
        }

        return FileType.Unknown;
    }
}
