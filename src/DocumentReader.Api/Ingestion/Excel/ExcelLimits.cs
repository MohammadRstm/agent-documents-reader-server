namespace DocumentReader.Api.Ingestion.Excel;

/// <summary>
/// Fixed bounds on Excel processing, protecting memory and CPU from very large or malicious
/// workbooks. Content beyond these limits is skipped and the document is marked partially indexed.
/// </summary>
public static class ExcelLimits
{
    /// <summary>Total uncompressed size of all parts of an .xlsx/.xlsm package.</summary>
    public const long MaxUncompressedPackageBytes = 100L * 1024 * 1024;

    /// <summary>Number of parts (files) inside an .xlsx/.xlsm package.</summary>
    public const int MaxPackageEntries = 10_000;

    /// <summary>Highest allowed compression ratio for a package part larger than 1 MB.</summary>
    public const int MaxCompressionRatio = 100;

    public const long CompressionRatioCheckThresholdBytes = 1024 * 1024;

    public const int MaxSheets = 100;

    public const int MaxCellsPerWorkbook = 1_000_000;

    public const int MaxRowNumber = 100_000;

    public const int MaxColumnNumber = 512;

    public const int MaxCellTextLength = 2_000;

    /// <summary>Merged ranges larger than this are ignored when spreading merged values.</summary>
    public const int MaxMergedRangeCells = 10_000;
}
