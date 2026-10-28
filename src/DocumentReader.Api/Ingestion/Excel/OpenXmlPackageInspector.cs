using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentReader.Api.Configuration;

namespace DocumentReader.Api.Ingestion.Excel;

public enum PackageSafety
{
    Safe,

    /// <summary>The package expands too much (a possible "zip bomb") or has too many parts.</summary>
    TooLarge,

    /// <summary>The ZIP structure cannot be read.</summary>
    Unreadable,
}

/// <summary>
/// Looks inside ZIP-based Office packages without fully extracting them: identifies spreadsheet
/// packages from [Content_Types].xml and checks declared sizes before any library opens the file.
/// </summary>
public static class OpenXmlPackageInspector
{
    private const string ContentTypesEntryName = "[Content_Types].xml";
    private const long MaxContentTypesBytes = 1024 * 1024;

    private const string XlsxWorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string XlsmWorkbookContentType = "application/vnd.ms-excel.sheet.macroEnabled.main+xml";

    /// <summary>Returns Xlsx or Xlsm for spreadsheet packages, otherwise Unknown (e.g. .docx or a plain ZIP).</summary>
    public static FileType DetectSpreadsheetType(byte[] content)
    {
        try
        {
            using var archive = OpenArchive(content);
            var contentTypes = archive.GetEntry(ContentTypesEntryName);
            if (contentTypes is null || contentTypes.Length > MaxContentTypesBytes)
            {
                return FileType.Unknown;
            }

            using var stream = contentTypes.Open();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(stream, settings);
            var declaredTypes = XDocument.Load(reader)
                .Descendants()
                .Where(element => element.Name.LocalName == "Override")
                .Select(element => (string?)element.Attribute("ContentType"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (declaredTypes.Contains(XlsmWorkbookContentType))
            {
                return FileType.Xlsm;
            }

            return declaredTypes.Contains(XlsxWorkbookContentType) ? FileType.Xlsx : FileType.Unknown;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException)
        {
            return FileType.Unknown;
        }
    }

    /// <summary>
    /// Checks the package's declared sizes against the workbook limits in <see cref="LimitsOptions"/>. .NET's ZIP reader does
    /// not return more data than an entry declares, so declared sizes are a meaningful bound.
    /// </summary>
    public static PackageSafety CheckSafety(byte[] content, LimitsOptions limits)
    {
        try
        {
            using var archive = OpenArchive(content);
            if (archive.Entries.Count > limits.MaxWorkbookPackageEntries)
            {
                return PackageSafety.TooLarge;
            }

            long totalUncompressed = 0;
            foreach (var entry in archive.Entries)
            {
                totalUncompressed += entry.Length;
                if (totalUncompressed > limits.MaxUncompressedWorkbookBytes)
                {
                    return PackageSafety.TooLarge;
                }

                var ratio = entry.CompressedLength == 0 ? double.MaxValue : (double)entry.Length / entry.CompressedLength;
                if (entry.Length > limits.WorkbookCompressionRatioCheckBytes && ratio > limits.MaxWorkbookCompressionRatio)
                {
                    return PackageSafety.TooLarge;
                }
            }

            return PackageSafety.Safe;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return PackageSafety.Unreadable;
        }
    }

    /// <summary>True when the package contains a VBA project (macros). Macros are never run.</summary>
    public static bool ContainsMacros(byte[] content)
    {
        try
        {
            using var archive = OpenArchive(content);
            return archive.Entries.Any(entry => entry.Name.Equals("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return false;
        }
    }

    private static ZipArchive OpenArchive(byte[] content) =>
        new(new MemoryStream(content, writable: false), ZipArchiveMode.Read, leaveOpen: false);
}
