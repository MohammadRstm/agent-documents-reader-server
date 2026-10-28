using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>Synthetic Excel files and packages generated in code.</summary>
public static class ExcelFixtures
{
    public const string XlsxWorkbookContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
    public const string XlsmWorkbookContentType = "application/vnd.ms-excel.sheet.macroEnabled.main+xml";

    /// <summary>
    /// Builds an .xlsx with ClosedXML. With <paramref name="saveFormulaResults"/> the formula results
    /// are calculated and stored in the file, as Excel does; without it formulas have no cached value.
    /// </summary>
    public static byte[] Xlsx(Action<XLWorkbook> build, bool saveFormulaResults = true)
    {
        using var workbook = new XLWorkbook();
        build(workbook);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = saveFormulaResults });
        return stream.ToArray();
    }

    /// <summary>
    /// A bill of quantities with an offset header (B3), a merged title, section headers, SUM subtotals,
    /// a repeated header, a grand total, a second table (milestones with dates) and a note.
    /// </summary>
    public static byte[] BillOfQuantities() => Xlsx(workbook =>
    {
        var sheet = workbook.AddWorksheet("BOQ");
        sheet.Cell("A1").Value = "Bill of Quantities - Tower B";
        sheet.Range("A1:F1").Merge();

        WriteHeader(sheet, 3);
        Bold(sheet.Cell("C4"), "1.0 Earthworks");
        Item(sheet, 5, "1.1", "Excavation", 120, "m3", 15);
        Item(sheet, 6, "1.2", "Backfilling", 80, "m3", 10);
        sheet.Cell("C7").Value = "Subtotal Earthworks";
        sheet.Cell("G7").FormulaA1 = "SUM(G5:G6)";

        WriteHeader(sheet, 9); // repeated header, e.g. after a page break
        Bold(sheet.Cell("C10"), "2.0 Concrete");
        Item(sheet, 11, "2.1", "Foundations", 50, "m3", 200);
        Item(sheet, 12, "2.2", "Slabs", 30, "m3", 180);
        sheet.Cell("C13").Value = "Subtotal Concrete";
        sheet.Cell("G13").FormulaA1 = "SUM(G11:G12)";
        sheet.Cell("C14").Value = "Grand Total";
        sheet.Cell("G14").FormulaA1 = "G7+G13";

        Bold(sheet.Cell("B17"), "Milestone");
        Bold(sheet.Cell("C17"), "Due date");
        sheet.Cell("B18").Value = "Handover";
        sheet.Cell("C18").Value = new DateTime(2027, 3, 15);
        sheet.Cell("B19").Value = "Final account";
        sheet.Cell("C19").Value = new DateTime(2027, 6, 30);

        sheet.Cell("A22").Value = "Note: prices exclude VAT.";
    });

    /// <summary>A ZIP package with a [Content_Types].xml that declares the given workbook content type.</summary>
    public static byte[] Package(string workbookContentType, params (string Name, string Content)[] parts)
    {
        var contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{workbookContentType}\"/>" +
            "</Types>";
        return Zip([("[Content_Types].xml", contentTypes), .. parts]);
    }

    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A spreadsheet package with one highly compressible part of the given size: a small file that
    /// expands enormously, like a "zip bomb".
    /// </summary>
    public static byte[] HighlyCompressedPackage(int uncompressedPartBytes)
    {
        var contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{XlsxWorkbookContentType}\"/>" +
            "</Types>";
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open(), Encoding.UTF8))
            {
                writer.Write(contentTypes);
            }

            using var part = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[64 * 1024];
            for (var written = 0; written < uncompressedPartBytes; written += zeros.Length)
            {
                part.Write(zeros, 0, Math.Min(zeros.Length, uncompressedPartBytes - written));
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Bytes that start with the OLE compound-file signature and contain a directory entry name.
    /// Enough for type detection, not a readable file.
    /// </summary>
    public static byte[] CompoundFile(string streamName)
    {
        var content = new byte[1024];
        byte[] signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        signature.CopyTo(content, 0);
        Encoding.Unicode.GetBytes(streamName + "\0").CopyTo(content, 512);
        return content;
    }

    private static void WriteHeader(IXLWorksheet sheet, int row)
    {
        string[] headers = ["Item", "Description", "Qty", "Unit", "Rate", "Amount (USD)"];
        for (var index = 0; index < headers.Length; index++)
        {
            Bold(sheet.Cell(row, 2 + index), headers[index]);
        }
    }

    private static void Item(IXLWorksheet sheet, int row, string code, string description, int quantity, string unit, int rate)
    {
        sheet.Cell(row, 2).Value = code;
        sheet.Cell(row, 3).Value = description;
        sheet.Cell(row, 4).Value = quantity;
        sheet.Cell(row, 5).Value = unit;
        sheet.Cell(row, 6).Value = rate;
        sheet.Cell(row, 7).FormulaA1 = $"D{row}*F{row}";
        sheet.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
    }

    private static void Bold(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Font.Bold = true;
    }
}
