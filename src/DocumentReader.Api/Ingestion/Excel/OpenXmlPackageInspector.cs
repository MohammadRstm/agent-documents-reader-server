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
    private const string PackageRelationshipsEntryName = "_rels/.rels";
    private const long MaxMetadataPartBytes = 1024 * 1024;

    private const string XlsxWorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    private const string XlsmWorkbookContentType = "application/vnd.ms-excel.sheet.macroEnabled.main+xml";

    /// <summary>
    /// Returns Xlsx or Xlsm for spreadsheet packages, otherwise Unknown (e.g. .docx or a plain ZIP).
    /// The workbook part is found through the package relationships (_rels/.rels) and its content type
    /// is resolved like an OPC reader does: an Override for the part name wins, otherwise the Default
    /// for its extension. Writers differ here: Excel uses an Override, while System.IO.Packaging (used
    /// by ClosedXML) declares the workbook type as the Default for ".xml".
    /// </summary>
    public static FileType DetectSpreadsheetType(byte[] content)
    {
        try
        {
            using var archive = OpenArchive(content);
            var contentTypes = LoadXml(archive, ContentTypesEntryName);
            if (contentTypes is null)
            {
                return FileType.Unknown;
            }

            var overrides = contentTypes.Descendants()
                .Where(element => element.Name.LocalName == "Override")
                .Select(element => (Key: NormalizePartName((string?)element.Attribute("PartName")), Type: (string?)element.Attribute("ContentType")))
                .Where(entry => entry.Key.Length > 1 && entry.Type is not null)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Type!, StringComparer.OrdinalIgnoreCase);
            var defaults = contentTypes.Descendants()
                .Where(element => element.Name.LocalName == "Default")
                .Select(element => (Key: ((string?)element.Attribute("Extension") ?? string.Empty).TrimStart('.'), Type: (string?)element.Attribute("ContentType")))
                .Where(entry => entry.Key.Length > 0 && entry.Type is not null)
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Type!, StringComparer.OrdinalIgnoreCase);

            var workbookPart = FindOfficeDocumentPart(archive);
            if (workbookPart is not null)
            {
                return ToSpreadsheetType(ResolveContentType(workbookPart, overrides, defaults));
            }

            // No usable package relationships: fall back to any declared spreadsheet workbook type.
            var declared = overrides.Values.Concat(defaults.Values).ToList();
            return declared.Select(ToSpreadsheetType).FirstOrDefault(type => type != FileType.Unknown, FileType.Unknown);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The ZIP structure itself is broken (for example a truncated download). If the raw bytes
            // still show spreadsheet part names, report it as a damaged workbook rather than as an
            // unsupported file type; the extractor then rejects it as unreadable.
            return LooksLikeDamagedSpreadsheet(content) ? FileType.Xlsx : FileType.Unknown;
        }
    }

    /// <summary>True when ZIP local headers in the raw bytes name spreadsheet parts ("xl/...").</summary>
    private static bool LooksLikeDamagedSpreadsheet(byte[] content) =>
        content.AsSpan().IndexOf("xl/"u8) >= 0;

    private static FileType ToSpreadsheetType(string? contentType) => contentType switch
    {
        _ when string.Equals(contentType, XlsmWorkbookContentType, StringComparison.OrdinalIgnoreCase) => FileType.Xlsm,
        _ when string.Equals(contentType, XlsxWorkbookContentType, StringComparison.OrdinalIgnoreCase) => FileType.Xlsx,
        _ => FileType.Unknown,
    };

    private static string? ResolveContentType(
        string partName,
        Dictionary<string, string> overrides,
        Dictionary<string, string> defaults)
    {
        if (overrides.TryGetValue(partName, out var overridden))
        {
            return overridden;
        }

        var extension = Path.GetExtension(partName).TrimStart('.');
        return defaults.TryGetValue(extension, out var byExtension) ? byExtension : null;
    }

    /// <summary>The target of the package's officeDocument relationship, as a part name like "/xl/workbook.xml".</summary>
    private static string? FindOfficeDocumentPart(ZipArchive archive)
    {
        var relationships = LoadXml(archive, PackageRelationshipsEntryName);
        var target = relationships?.Descendants()
            .Where(element => element.Name.LocalName == "Relationship")
            .Where(element => ((string?)element.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase) == true)
            .Select(element => (string?)element.Attribute("Target"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return target is null ? null : NormalizePartName(target);
    }

    /// <summary>Normalizes "xl/workbook.xml", "/xl/workbook.xml" and "./xl/workbook.xml" to "/xl/workbook.xml".</summary>
    private static string NormalizePartName(string? partName)
    {
        var name = (partName ?? string.Empty).Trim().Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        return "/" + name.TrimStart('/');
    }

    /// <summary>Loads a small XML entry safely (no DTDs, no external resolution); null if missing or too large.</summary>
    private static XDocument? LoadXml(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null || entry.Length > MaxMetadataPartBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
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
        catch (Exception exception) when (exception is not OperationCanceledException)
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
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static ZipArchive OpenArchive(byte[] content) =>
        new(new MemoryStream(content, writable: false), ZipArchiveMode.Read, leaveOpen: false);
}
