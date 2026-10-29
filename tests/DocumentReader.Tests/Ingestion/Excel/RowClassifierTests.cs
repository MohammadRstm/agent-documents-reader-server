using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Tests.Ingestion.Excel;

public sealed class RowClassifierTests
{
    [Fact]
    public void Classify_DescriptionWithNumbers_IsItem()
    {
        Assert.Equal(RowKind.Item, Classify(4, Text(1, "Excavation"), Number(2, 120), Number(3, 1800)));
    }

    [Fact]
    public void Classify_DescriptionStartingWithTotal_IsStillItem()
    {
        Assert.Equal(RowKind.Item, Classify(4, Text(1, "Total station hire"), Number(2, 5), Number(3, 900)));
    }

    [Theory]
    [InlineData("Total")]
    [InlineData("Grand Total")]
    [InlineData("TOTAL AMOUNT")]
    [InlineData("Total (USD):")]
    public void Classify_UnqualifiedTotalLabel_IsTotal(string label)
    {
        Assert.Equal(RowKind.Total, Classify(4, Text(1, label), Number(3, 18000)));
    }

    [Theory]
    [InlineData("Subtotal")]
    [InlineData("Sub-total Earthworks")]
    [InlineData("Carried forward")]
    [InlineData("Section 2 Total")]
    public void Classify_QualifiedTotalLabel_IsSubtotal(string label)
    {
        Assert.Equal(RowKind.Subtotal, Classify(4, Text(1, label), Number(3, 2600)));
    }

    [Theory]
    [InlineData("Section 1 subtotal")]
    [InlineData("Subtotal - Earthworks")]
    [InlineData("Sub-total")]
    [InlineData("Sub total")]
    [InlineData("SUBTOTAL")]
    [InlineData("Total Section 2")]
    [InlineData("Total for Section 2")]
    [InlineData("Earthworks total")]
    [InlineData("Total carried to summary")]
    [InlineData("Carried forward")]
    [InlineData("Brought forward")]
    public void Classify_SubtotalLabelInAnyColumnWithOnlyAnAmount_IsSubtotal(string label)
    {
        Assert.Equal(RowKind.Subtotal, Classify(6, Text(2, label, bold: true), Number(6, 14700)));
    }

    [Fact]
    public void Classify_SubtotalWordWithoutValue_IsStillSubtotal()
    {
        Assert.Equal(RowKind.Subtotal, Classify(6, Text(2, "Section 1 subtotal")));
    }

    [Theory]
    [InlineData("Grand total")]
    [InlineData("Total")]
    [InlineData("Contract total")]
    [InlineData("Total amount")]
    public void Classify_TableTotalLabel_IsTotal(string label)
    {
        Assert.Equal(RowKind.Total, Classify(6, Text(2, label), Number(6, 62800)));
    }

    [Fact]
    public void Classify_TotalStationHireItemRow_StaysItem()
    {
        Assert.Equal(
            RowKind.Item,
            Classify(6, Text(1, "3.1"), Text(2, "Total station hire"), Text(3, "day"), Number(4, 5), Number(5, 180), Number(6, 900)));
    }

    [Fact]
    public void Classify_SummaryShapedRowWithTotalInLabel_IsSubtotal()
    {
        Assert.Equal(RowKind.Subtotal, Classify(6, Text(2, "Concrete works - totals"), Number(6, 48100)));
    }

    [Fact]
    public void Classify_NumbersThatAreAllSumFormulas_IsSubtotal()
    {
        Assert.Equal(RowKind.Subtotal, Classify(4, Number(3, 2600, formula: "SUM(C2:C3)")));
    }

    [Fact]
    public void Classify_SingleTextCellInWideTable_IsSectionHeader()
    {
        Assert.Equal(RowKind.SectionHeader, Classify(4, Text(1, "2.0 Concrete")));
    }

    [Fact]
    public void Classify_SingleTextCellInNarrowTable_IsUnknown()
    {
        Assert.Equal(RowKind.Unknown, Classify(2, Text(1, "See note")));
    }

    private static RowKind Classify(int tableColumnCount, params GridCell[] cells) =>
        RowClassifier.Classify(cells, tableColumnCount, stylesAvailable: true);

    private static GridCell Text(int column, string text, bool bold = false) =>
        new(1, column, CellValueKind.Text, text, null, null, null, null, bold);

    private static GridCell Number(int column, decimal value, string? formula = null) =>
        new(1, column, CellValueKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture), value, null, formula, null, false);
}
