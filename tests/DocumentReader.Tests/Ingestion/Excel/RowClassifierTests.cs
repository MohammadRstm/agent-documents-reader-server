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

    private static GridCell Text(int column, string text) =>
        new(1, column, CellValueKind.Text, text, null, null, null, null, false);

    private static GridCell Number(int column, decimal value, string? formula = null) =>
        new(1, column, CellValueKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture), value, null, formula, null, false);
}
