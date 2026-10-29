using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.SS.Util;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>Synthetic legacy .xls (BIFF8) workbooks generated with NPOI (test-only dependency).</summary>
public static class XlsFixtures
{
    /// <summary>
    /// Builds an .xls. Formulas are evaluated before saving so the file contains their results,
    /// like a workbook saved by Excel (NPOI would otherwise store 0).
    /// </summary>
    public static byte[] Xls(Action<HSSFWorkbook> build)
    {
        var workbook = new HSSFWorkbook();
        build(workbook);
        HSSFFormulaEvaluator.EvaluateAllFormulaCells(workbook);
        using var stream = new MemoryStream();
        workbook.Write(stream, leaveOpen: true);
        return stream.ToArray();
    }

    /// <summary>The same bill of quantities as <see cref="ExcelFixtures.BillOfQuantities"/>, saved as .xls.</summary>
    public static byte[] BillOfQuantities() => Xls(workbook =>
    {
        var sheet = workbook.CreateSheet("BOQ");
        var bold = BoldStyle(workbook);
        var amountStyle = workbook.CreateCellStyle();
        amountStyle.DataFormat = workbook.CreateDataFormat().GetFormat("#,##0.00");
        var dateStyle = workbook.CreateCellStyle();
        dateStyle.DataFormat = HSSFDataFormat.GetBuiltinFormat("m/d/yy");

        Text(sheet, "A1", "Bill of Quantities - Tower B");
        sheet.AddMergedRegion(CellRangeAddress.ValueOf("A1:F1"));

        WriteHeader(sheet, 3, bold);
        Text(sheet, "C4", "1.0 Earthworks", bold);
        Item(sheet, 5, "1.1", "Excavation", 120, "m3", 15, amountStyle);
        Item(sheet, 6, "1.2", "Backfilling", 80, "m3", 10, amountStyle);
        Text(sheet, "C7", "Subtotal Earthworks");
        Formula(sheet, "G7", "SUM(G5:G6)");

        WriteHeader(sheet, 9, bold);
        Text(sheet, "C10", "2.0 Concrete", bold);
        Item(sheet, 11, "2.1", "Foundations", 50, "m3", 200, amountStyle);
        Item(sheet, 12, "2.2", "Slabs", 30, "m3", 180, amountStyle);
        Text(sheet, "C13", "Subtotal Concrete");
        Formula(sheet, "G13", "SUM(G11:G12)");
        Text(sheet, "C14", "Grand Total");
        Formula(sheet, "G14", "G7+G13");

        Text(sheet, "B17", "Milestone", bold);
        Text(sheet, "C17", "Due date", bold);
        Text(sheet, "B18", "Handover");
        Date(sheet, "C18", new DateTime(2027, 3, 15), dateStyle);
        Text(sheet, "B19", "Final account");
        Date(sheet, "C19", new DateTime(2027, 6, 30), dateStyle);

        Text(sheet, "A22", "Note: prices exclude VAT.");
    });

    public static ICell Cell(ISheet sheet, string address)
    {
        var reference = new CellReference(address);
        var row = sheet.GetRow(reference.Row) ?? sheet.CreateRow(reference.Row);
        return row.GetCell(reference.Col) ?? row.CreateCell(reference.Col);
    }

    public static void Text(ISheet sheet, string address, string value, ICellStyle? style = null)
    {
        var cell = Cell(sheet, address);
        cell.SetCellValue(value);
        if (style is not null)
        {
            cell.CellStyle = style;
        }
    }

    public static void Number(ISheet sheet, string address, double value, ICellStyle? style = null)
    {
        var cell = Cell(sheet, address);
        cell.SetCellValue(value);
        if (style is not null)
        {
            cell.CellStyle = style;
        }
    }

    public static ICellStyle BoldStyle(IWorkbook workbook)
    {
        var font = workbook.CreateFont();
        font.IsBold = true;
        var style = workbook.CreateCellStyle();
        style.SetFont(font);
        return style;
    }

    private static void Date(ISheet sheet, string address, DateTime value, ICellStyle style)
    {
        var cell = Cell(sheet, address);
        cell.SetCellValue(value);
        cell.CellStyle = style;
    }

    private static void Formula(ISheet sheet, string address, string formula, ICellStyle? style = null)
    {
        var cell = Cell(sheet, address);
        cell.SetCellFormula(formula);
        if (style is not null)
        {
            cell.CellStyle = style;
        }
    }

    private static void WriteHeader(ISheet sheet, int row, ICellStyle bold)
    {
        string[] headers = ["Item", "Description", "Qty", "Unit", "Rate", "Amount (USD)"];
        for (var index = 0; index < headers.Length; index++)
        {
            Text(sheet, $"{(char)('B' + index)}{row}", headers[index], bold);
        }
    }

    private static void Item(ISheet sheet, int row, string code, string description, int quantity, string unit, int rate, ICellStyle amountStyle)
    {
        Text(sheet, $"B{row}", code);
        Text(sheet, $"C{row}", description);
        Number(sheet, $"D{row}", quantity);
        Text(sheet, $"E{row}", unit);
        Number(sheet, $"F{row}", rate);
        Formula(sheet, $"G{row}", $"D{row}*F{row}", amountStyle);
    }
}
