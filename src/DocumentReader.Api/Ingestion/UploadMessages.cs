namespace DocumentReader.Api.Ingestion;

/// <summary>
/// User-facing warnings for per-file outcomes. Each one says what happened and what the user can do,
/// and none contains internal details (exception text, paths, library names).
/// </summary>
public static class UploadMessages
{
    public const string Unsupported =
        "This file is not a PDF or Excel file (.pdf, .xlsx, .xlsm, .xls), so it was not processed. Convert it to one of these formats and upload it again.";

    public const string NamedLikeWorkbookButUnreadable =
        "This file is named like an Excel workbook, but its content could not be read as one. It may be damaged, or a different kind of file that was renamed. Open it in Excel, save a new copy, and upload that copy.";

    public const string NamedLikePdfButUnreadable =
        "This file is named like a PDF, but its content could not be read as one. It may be damaged, or a different kind of file that was renamed. Export or download it again, then upload the new copy.";

    public const string Empty = "The file is empty (0 bytes). Upload a file that has content.";

    public const string Damaged =
        "The file could not be read because it appears to be damaged. Check the file and upload it again.";

    public const string PdfDamaged =
        "The PDF could not be read because it appears to be damaged or incomplete. Export or download it again, then upload the new copy.";

    public const string PdfPasswordProtected =
        "The PDF is password-protected, so its content could not be read. Remove the password and upload it again.";

    public const string PdfNoPages = "The PDF has no pages, so there is nothing to read.";

    public const string PdfNoText = "The PDF contains no text to read.";

    public const string PdfScanned =
        "No text could be extracted: the PDF appears to be scanned (its pages are images), and text recognition (OCR) is not supported. Upload a PDF with selectable text.";

    public const string WorkbookDamaged =
        "The workbook could not be read because it appears to be damaged or incomplete. Open it in Excel, save a new copy, and upload that copy.";

    public const string OfficePasswordProtected =
        "The file is password-protected, so its content could not be read. Remove the password and upload it again.";

    public const string WorkbookNoData = "The workbook contains no data.";

    public const string TimedOut =
        "The file could not be processed within the time limit. Try a smaller file, or split it into several files.";

    public static string TooLarge(long limitBytes) =>
        $"The file is larger than the {SizeFormatter.Format(limitBytes)} limit, so it was not processed. Reduce its size or split it into smaller files, then upload it again.";

    public static string WorkbookTooLargeUncompressed(long limitBytes) =>
        $"The workbook is too large to process once uncompressed (more than {SizeFormatter.Format(limitBytes)} of data, or too many or too highly compressed parts), so it was not processed. Remove unused sheets or data, or split it into smaller workbooks, then upload it again.";
}
