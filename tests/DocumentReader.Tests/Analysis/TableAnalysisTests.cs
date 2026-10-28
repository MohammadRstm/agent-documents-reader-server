using DocumentReader.Api.Analysis;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Analysis;

public sealed class TableAnalysisTests
{
    private static readonly HashSet<RowKind> ItemsOnly = [RowKind.Item];

    private readonly DetectedTable _boq = TestSessions.Workbook(ExcelFixtures.BillOfQuantities()).Tables[0];

    [Fact]
    public void Sum_ItemsOnly_ExcludesSubtotalsAndTotals()
    {
        var result = Aggregate(_boq, "Amount (USD)", AggregateOperation.Sum, [], ItemsOnly);

        Assert.Null(result.Error);
        Assert.Equal(18000m, result.Total!.Result);
        Assert.Equal(4, result.Total.ValuesUsed);
        Assert.Equal([5, 6, 11, 12], result.Total.ContributingRows);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public void Sum_IncludingSubtotalAndTotalRows_CountsThemExplicitly()
    {
        var result = Aggregate(_boq, "Amount (USD)", AggregateOperation.Sum, [], new HashSet<RowKind> { RowKind.Item, RowKind.Subtotal, RowKind.Total });

        Assert.Equal(54000m, result.Total!.Result);
    }

    [Fact]
    public void Sum_WithFilter_UsesOnlyMatchingRows()
    {
        var filter = new RowFilter(Column(_boq, "Qty"), FilterOperator.Gte, FilterValue.FromNumber(80));

        var result = Aggregate(_boq, "Amount (USD)", AggregateOperation.Sum, [filter], ItemsOnly);

        Assert.Equal(2600m, result.Total!.Result);
        Assert.Equal([5, 6], result.Total.ContributingRows);
    }

    [Fact]
    public void Average_Min_Max_Count_AreExact()
    {
        Assert.Equal(4500m, Aggregate(_boq, "Amount (USD)", AggregateOperation.Average, [], ItemsOnly).Total!.Result);
        Assert.Equal(4m, Aggregate(_boq, "Description", AggregateOperation.Count, [], ItemsOnly).Total!.Result);

        var max = Aggregate(_boq, "Amount (USD)", AggregateOperation.Max, [], ItemsOnly).Total!;
        Assert.Equal(10000m, max.Result);
        Assert.Equal([11], max.ResultRows);

        var min = Aggregate(_boq, "Amount (USD)", AggregateOperation.Min, [], ItemsOnly).Total!;
        Assert.Equal(800m, min.Result);
        Assert.Equal([6], min.ResultRows);
    }

    [Fact]
    public void Sum_DecimalValues_HaveNoBinaryRoundingErrors()
    {
        var table = SingleTable(ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Small");
            sheet.Cell("A1").Value = "Item";
            sheet.Cell("B1").Value = "Price";
            sheet.Cell("A2").Value = "First";
            sheet.Cell("B2").Value = 0.1;
            sheet.Cell("A3").Value = "Second";
            sheet.Cell("B3").Value = 0.2;
        }));

        Assert.Equal(0.3m, Aggregate(table, "Price", AggregateOperation.Sum, [], ItemsOnly).Total!.Result);
    }

    [Fact]
    public void Sum_MixedCurrencies_IsRefused()
    {
        var table = SingleTable(ExcelFixtures.Xlsx(book =>
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
        }));

        var result = Aggregate(table, "Price", AggregateOperation.Sum, [], ItemsOnly);

        Assert.NotNull(result.Error);
        Assert.Contains("different currencies", result.Error);
        Assert.Null(result.Total);
    }

    [Fact]
    public void Sum_HeaderAndFormatCurrenciesDisagree_IsRefused()
    {
        var table = SingleTable(ExcelFixtures.Xlsx(book =>
        {
            var sheet = book.AddWorksheet("Prices");
            sheet.Cell("A1").Value = "Item";
            sheet.Cell("B1").Value = "Price (USD)";
            sheet.Cell("A2").Value = "Pump";
            sheet.Cell("B2").Value = 100;
            sheet.Cell("B2").Style.NumberFormat.Format = "£#,##0.00";
        }));

        Assert.Contains("ambiguous", Aggregate(table, "Price (USD)", AggregateOperation.Sum, [], ItemsOnly).Error);
    }

    [Fact]
    public void Count_IgnoresCurrency()
    {
        var table = SingleTable(ExcelFixtures.Xlsx(book =>
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
        }));

        Assert.Equal(2m, Aggregate(table, "Price", AggregateOperation.Count, [], ItemsOnly).Total!.Result);
    }

    [Fact]
    public void GroupBy_ReturnsOneResultPerValue()
    {
        var result = Aggregate(_boq, "Amount (USD)", AggregateOperation.Sum, [], ItemsOnly, groupBy: "Unit");

        var group = Assert.Single(result.Groups);
        Assert.Equal("m3", group.Key);
        Assert.Equal(18000m, group.Value.Result);
    }

    [Fact]
    public void Query_FiltersAndSortsAllRows()
    {
        var filter = new RowFilter(Column(_boq, "Description"), FilterOperator.Contains, FilterValue.FromText("fill"));
        Assert.Equal([6], TableQuery.Run(_boq, [filter], null, null, false, 10).Rows.Select(row => row.RowNumber));

        var sorted = TableQuery.Run(_boq, [], ItemsOnly, Column(_boq, "Amount (USD)"), sortDescending: true, limit: 2);
        Assert.Equal(4, sorted.TotalMatches);
        Assert.True(sorted.IsTruncated);
        Assert.Equal([11, 12], sorted.Rows.Select(row => row.RowNumber));
    }

    [Fact]
    public void Query_IsEmptyAndDateFilters_Work()
    {
        var empty = new RowFilter(Column(_boq, "Qty"), FilterOperator.IsEmpty, null);
        Assert.Equal([4, 7, 10, 13, 14], TableQuery.Run(_boq, [empty], null, null, false, 50).Rows.Select(row => row.RowNumber));

        var milestones = TestSessions.Workbook(ExcelFixtures.BillOfQuantities()).Tables[1];
        var before = new RowFilter(Column(milestones, "Due date"), FilterOperator.Lt, FilterValue.FromText("2027-04-01"));
        Assert.Equal([18], TableQuery.Run(milestones, [before], null, null, false, 50).Rows.Select(row => row.RowNumber));
    }

    private static AggregateResult Aggregate(
        DetectedTable table,
        string column,
        AggregateOperation operation,
        IReadOnlyList<RowFilter> filters,
        IReadOnlySet<RowKind> rowKinds,
        string? groupBy = null) =>
        TableAggregator.Aggregate(table, Column(table, column), operation, filters, rowKinds, groupBy is null ? null : Column(table, groupBy));

    private static TableColumn Column(DetectedTable table, string name) =>
        table.Columns.Single(column => column.Name == name);

    private static DetectedTable SingleTable(byte[] xlsx) => Assert.Single(TestSessions.Workbook(xlsx).Tables);
}
