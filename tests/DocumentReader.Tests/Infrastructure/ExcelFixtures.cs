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

    /// <summary>
    /// A BOQ whose section subtotals and grand total are typed-in values (not formulas) labelled
    /// "Section N subtotal" in column B, with "$"-formatted amounts under an "Amount (USD)" header.
    /// Item amounts: 3000 + 8100 + 3600 = 14700 and 17400 + 17500 + 13200 = 48100; grand total 62800.
    /// </summary>
    public static byte[] SectionedBoqWithValueSubtotals() => Xlsx(workbook =>
    {
        var sheet = workbook.AddWorksheet("BOQ");
        sheet.Cell("A1").Value = "Bill of Quantities";
        string[] headers = ["Item", "Description", "Unit", "Qty", "Rate (USD)", "Amount (USD)"];
        for (var index = 0; index < headers.Length; index++)
        {
            Bold(sheet.Cell(4, 1 + index), headers[index]);
        }

        Bold(sheet.Cell("B5"), "Section 1 - Earthworks");
        PricedItem(sheet, 6, "1.1", "Excavation", "m3", 100, 30);
        PricedItem(sheet, 7, "1.2", "Disposal", "m3", 270, 30);
        PricedItem(sheet, 8, "1.3", "Backfilling", "m3", 120, 30);
        Bold(sheet.Cell("B9"), "Section 1 subtotal");
        BoldAmount(sheet.Cell("F9"), 14700);

        Bold(sheet.Cell("B10"), "Section 2 - Concrete");
        PricedItem(sheet, 11, "2.1", "Foundations", "m3", 58, 300);
        PricedItem(sheet, 12, "2.2", "Columns", "m3", 70, 250);
        PricedItem(sheet, 13, "2.3", "Slabs", "m2", 110, 120);
        Bold(sheet.Cell("B14"), "Section 2 subtotal");
        BoldAmount(sheet.Cell("F14"), 48100);

        Bold(sheet.Cell("B15"), "Grand Total");
        BoldAmount(sheet.Cell("F15"), 62800);
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

    /// <summary>
    /// A package with the given content-type declarations (the inside of &lt;Types&gt;) and an
    /// officeDocument relationship pointing at <paramref name="officeDocumentTarget"/>.
    /// </summary>
    public static byte[] PackageWithRelationships(string typeDeclarations, string officeDocumentTarget)
    {
        var contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">{typeDeclarations}</Types>";
        var relationships =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" " +
            $"Target=\"{officeDocumentTarget}\"/></Relationships>";
        return Zip(("[Content_Types].xml", contentTypes), ("_rels/.rels", relationships));
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

    private static void PricedItem(IXLWorksheet sheet, int row, string code, string description, string unit, int quantity, int rate)
    {
        sheet.Cell(row, 1).Value = code;
        sheet.Cell(row, 2).Value = description;
        sheet.Cell(row, 3).Value = unit;
        sheet.Cell(row, 4).Value = quantity;
        sheet.Cell(row, 5).Value = rate;
        sheet.Cell(row, 5).Style.NumberFormat.Format = "\"$\"#,##0.00";
        sheet.Cell(row, 6).Value = quantity * rate;
        sheet.Cell(row, 6).Style.NumberFormat.Format = "\"$\"#,##0.00";
    }

    private static void BoldAmount(IXLCell cell, int value)
    {
        cell.Value = value;
        cell.Style.Font.Bold = true;
        cell.Style.NumberFormat.Format = "\"$\"#,##0.00";
    }

    private static void Bold(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Font.Bold = true;
    }
}
