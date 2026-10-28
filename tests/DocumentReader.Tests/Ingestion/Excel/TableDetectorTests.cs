using DocumentReader.Api.Configuration;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace DocumentReader.Tests.Ingestion.Excel;

public sealed class TableDetectorTests
{
    [Fact]
    public void Detect_BillOfQuantities_FindsBothTablesWithOffsetHeader()
    {
        var layout = DetectFirstSheet(ExcelFixtures.BillOfQuantities());

        Assert.Equal(2, layout.Tables.Count);
        var boq = layout.Tables[0];
        Assert.Equal("B3:G14", boq.Range);
        Assert.Equal(["Item", "Description", "Qty", "Unit", "Rate", "Amount (USD)"], boq.Columns.Select(column => column.Name));
        Assert.Equal("B17:C19", layout.Tables[1].Range);
    }

    [Fact]
    public void Detect_BillOfQuantities_ClassifiesRowsAndSkipsRepeatedHeader()
    {
        var boq = DetectFirstSheet(ExcelFixtures.BillOfQuantities()).Tables[0];

        Assert.Equal(
            [
                (4, RowKind.SectionHeader),
                (5, RowKind.Item),
                (6, RowKind.Item),
                (7, RowKind.Subtotal),
                (10, RowKind.SectionHeader),
                (11, RowKind.Item),
                (12, RowKind.Item),
                (13, RowKind.Subtotal),
                (14, RowKind.Total),
            ],
            boq.Rows.Select(row => (row.RowNumber, row.Kind)));
        Assert.Contains(boq.Warnings, warning => warning.Contains("repeated header"));
    }

    [Fact]
    public void Detect_BillOfQuantities_InfersColumnTypesAndCurrencyFromHeader()
    {
        var layout = DetectFirstSheet(ExcelFixtures.BillOfQuantities());
        var columns = layout.Tables[0].Columns.ToDictionary(column => column.Name);

        Assert.Equal(ColumnType.Text, columns["Description"].Type);
        Assert.Equal(ColumnType.Number, columns["Qty"].Type);
        Assert.Null(columns["Qty"].Currency);
        Assert.Equal(ColumnType.Number, columns["Amount (USD)"].Type);
        Assert.Equal("USD", columns["Amount (USD)"].Currency);
        Assert.Equal(ColumnType.Date, layout.Tables[1].Columns.Single(column => column.Name == "Due date").Type);
    }

    [Fact]
    public void Detect_BillOfQuantities_UsesCachedFormulaValues()
    {
        var boq = DetectFirstSheet(ExcelFixtures.BillOfQuantities()).Tables[0];
        var amountColumn = boq.Columns.Single(column => column.Name == "Amount (USD)").ColumnNumber;

        Assert.Equal(1800m, boq.Rows.Single(row => row.RowNumber == 5).Cells[amountColumn].Number);
        Assert.Equal(18000m, boq.Rows.Single(row => row.RowNumber == 14).Cells[amountColumn].Number);
    }

    [Fact]
    public void Detect_RowsOutsideTables_AreReportedSeparately()
    {
        var layout = DetectFirstSheet(ExcelFixtures.BillOfQuantities());

        Assert.Equal([1, 22], layout.RowsOutsideTables);
    }

    [Fact]
    public void Detect_TwoRowHeaderWithMergedGroup_CombinesNames()
    {
        var workbook = ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Costs");
            sheet.Cell("A1").Value = "Item";
            sheet.Range("A1:A2").Merge();
            sheet.Cell("B1").Value = "Cost";
            sheet.Range("B1:C1").Merge();
            sheet.Cell("B2").Value = "Labour";
            sheet.Cell("C2").Value = "Material";
            sheet.Cell("A3").Value = "Walls";
            sheet.Cell("B3").Value = 100;
            sheet.Cell("C3").Value = 250;
            sheet.Cell("A4").Value = "Roof";
            sheet.Cell("B4").Value = 80;
            sheet.Cell("C4").Value = 300;
        });

        var table = Assert.Single(DetectFirstSheet(workbook).Tables);

        Assert.Equal(1, table.HeaderFirstRow);
        Assert.Equal(2, table.HeaderLastRow);
        Assert.Equal(["Item", "Cost / Labour", "Cost / Material"], table.Columns.Select(column => column.Name));
        Assert.Equal([3, 4], table.Rows.Select(row => row.RowNumber));
    }

    [Fact]
    public void Detect_VerticallyMergedNumber_IsNotCopiedToOtherRows()
    {
        var workbook = ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Lump sums");
            sheet.Cell("A1").Value = "Package";
            sheet.Cell("B1").Value = "Amount";
            sheet.Cell("A2").Value = "Design";
            sheet.Cell("A3").Value = "Supervision";
            sheet.Cell("B2").Value = 5000;
            sheet.Range("B2:B3").Merge();
        });

        var table = Assert.Single(DetectFirstSheet(workbook).Tables);

        Assert.True(table.Rows.Single(row => row.RowNumber == 2).Cells.ContainsKey(2));
        Assert.False(table.Rows.Single(row => row.RowNumber == 3).Cells.ContainsKey(2));
    }

    [Fact]
    public void Detect_ColumnWithDifferentCurrencyFormats_HasNoCurrencyAndWarns()
    {
        var workbook = ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Prices");
            sheet.Cell("A1").Value = "Item";
            sheet.Cell("B1").Value = "Price";
            sheet.Cell("A2").Value = "Pump";
            sheet.Cell("B2").Value = 100;
            sheet.Cell("B2").Style.NumberFormat.Format = "[$€-x-euro2] #,##0.00";
            sheet.Cell("A3").Value = "Valve";
            sheet.Cell("B3").Value = 200;
            sheet.Cell("B3").Style.NumberFormat.Format = "\"AED\" #,##0.00";
        });

        var table = Assert.Single(DetectFirstSheet(workbook).Tables);

        Assert.Null(table.Columns.Single(column => column.Name == "Price").Currency);
        Assert.Contains(table.Warnings, warning => warning.Contains("more than one currency"));
    }

    [Fact]
    public void Detect_ValuesInColumnWithoutHeader_AddsNamedColumnAndWarns()
    {
        var workbook = ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Data");
            sheet.Cell("A1").Value = "Name";
            sheet.Cell("B1").Value = "Value";
            sheet.Cell("A2").Value = "First";
            sheet.Cell("B2").Value = 1;
            sheet.Cell("C2").Value = 99;
        });

        var table = Assert.Single(DetectFirstSheet(workbook).Tables);

        Assert.Contains(table.Columns, column => column.Name == "Column C");
        Assert.Contains(table.Warnings, warning => warning.Contains("have no header"));
    }

    [Fact]
    public void Detect_SheetWithoutHeader_HasNoTablesAndKeepsRowsAsText()
    {
        var workbook = ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Numbers");
            sheet.Cell("A1").Value = 1;
            sheet.Cell("A2").Value = 2;
        });

        var layout = DetectFirstSheet(workbook);

        Assert.Empty(layout.Tables);
        Assert.Equal([1, 2], layout.RowsOutsideTables);
    }

    private static SheetLayout DetectFirstSheet(byte[] xlsx)
    {
        var grid = new XlsxGridReader(Options.Create(new LimitsOptions())).Read(xlsx, CancellationToken.None);
        return TableDetector.Detect(grid.Sheets[0], new LimitsOptions());
    }
}
