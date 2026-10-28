using System.Text.Json;
using DocumentReader.Api.Agent.Tools;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocumentReader.Tests.Agent;

/// <summary>Runs the agent tools directly against in-memory sessions; no LLM is involved.</summary>
public sealed class ToolTests
{
    private readonly ToolRegistry _registry = new(
        [
            new ListDocumentsTool(),
            new SearchTextTool(),
            new GetEvidenceTool(),
            new DescribeTableTool(),
            new QueryTableTool(),
            new AggregateTableTool(),
            new FindDatesTool(),
        ],
        NullLogger<ToolRegistry>.Instance);

    private readonly Session _session = TestSessions.Create(
        TestSessions.TextDocument("contract.pdf", "Payment terms: 30 days from invoice.", "Completion date is 30 June 2027."),
        TestSessions.Workbook(ExcelFixtures.BillOfQuantities()));

    [Fact]
    public void Registry_ExposesTheSevenApprovedTools()
    {
        Assert.Equal(
            ["aggregate_table", "describe_table", "find_dates", "get_evidence", "list_documents", "query_table", "search_text"],
            _registry.Tools.Select(tool => tool.Name));
        Assert.All(_registry.Tools, tool => Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString()));
    }

    [Fact]
    public void Registry_UnknownTool_ReturnsError()
    {
        var error = TestSessions.Error(Run("delete_everything", "{}"));

        Assert.Contains("Unknown tool", error);
    }

    [Theory]
    [InlineData("search_text", """{ "query": "x", "path": "/etc/passwd" }""", "Unknown argument")]
    [InlineData("search_text", """[]""", "must be a JSON object")]
    [InlineData("search_text", """{ "query": 5 }""", "must be a string")]
    [InlineData("search_text", """{ "query": "x", "limit": 1000 }""", "from 1 to 20")]
    [InlineData("list_documents", """{ "anything": true }""", "Unknown argument")]
    [InlineData("aggregate_table", """{ "tableId": "T1", "column": "Qty", "operation": "median" }""", "must be one of")]
    [InlineData("query_table", """{ "tableId": "T1", "filters": [{ "column": "Description", "operator": "gt", "value": 5 }] }""", "numeric or date column")]
    [InlineData("query_table", """{ "tableId": "T1", "filters": [{ "column": "Nope", "operator": "eq", "value": 5 }] }""", "does not exist")]
    [InlineData("find_dates", """{ "from": "15/03/2027" }""", "yyyy-MM-dd")]
    public void InvalidArguments_ReturnSafeErrors(string tool, string input, string expected)
    {
        var error = TestSessions.Error(Run(tool, input));

        Assert.Contains(expected, error);
    }

    [Fact]
    public void ListDocuments_ShowsDocumentsAndTables()
    {
        var result = TestSessions.Success(Run("list_documents", "{}"));

        Assert.Equal(2, result.GetProperty("documentCount").GetInt32());
        var workbook = result.GetProperty("documents")[1];
        Assert.Equal("boq.xlsx", workbook.GetProperty("fileName").GetString());
        var tables = workbook.GetProperty("tables");
        Assert.Equal("T1", tables[0].GetProperty("tableId").GetString());
        Assert.Equal("B3:G14", tables[0].GetProperty("range").GetString());
        Assert.Equal("T2", tables[1].GetProperty("tableId").GetString());
    }

    [Fact]
    public void SearchText_ReturnsRankedEvidenceWithSources()
    {
        var result = Run("search_text", """{ "query": "payment terms" }""");
        var json = TestSessions.Success(result);

        var first = json.GetProperty("results")[0];
        Assert.Equal("E1", first.GetProperty("evidenceId").GetString());
        Assert.Equal("pdfPage", first.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(1, first.GetProperty("source").GetProperty("page").GetInt32());
        Assert.Contains("E1", result.EvidenceIds);
    }

    [Fact]
    public void SearchText_UnknownDocumentId_IsRejected()
    {
        var error = TestSessions.Error(Run("search_text", """{ "query": "payment", "documentIds": ["0123456789abcdef0123456789abcdef"] }"""));

        Assert.Contains("Unknown document ID", error);
    }

    [Fact]
    public void GetEvidence_ReturnsFullTextAndReportsUnknownIds()
    {
        var json = TestSessions.Success(Run("get_evidence", """{ "evidenceIds": ["E2", "E999", "../E1"] }"""));

        var evidence = Assert.Single(json.GetProperty("evidence").EnumerateArray());
        Assert.Equal("Completion date is 30 June 2027.", evidence.GetProperty("text").GetString());
        Assert.Equal(["E999", "../E1"], json.GetProperty("notFound").EnumerateArray().Select(id => id.GetString()));
    }

    [Fact]
    public void Tools_CannotReachAnotherSessionsData()
    {
        var otherSession = TestSessions.Create(TestSessions.TextDocument("other.pdf", "Secret salary data"));

        var evidence = TestSessions.Success(_registry.Execute(otherSession, "get_evidence", TestSessions.Json("""{ "evidenceIds": ["E3"] }"""), CancellationToken.None));
        Assert.Empty(evidence.GetProperty("evidence").EnumerateArray());

        var table = TestSessions.Error(_registry.Execute(otherSession, "describe_table", TestSessions.Json("""{ "tableId": "T1" }"""), CancellationToken.None));
        Assert.Contains("was not found", table);

        var search = TestSessions.Success(Run("search_text", """{ "query": "salary" }"""));
        Assert.Empty(search.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public void DescribeTable_ReportsColumnsAndRowKinds()
    {
        var json = TestSessions.Success(Run("describe_table", """{ "tableId": "T1" }"""));

        var amount = json.GetProperty("columns").EnumerateArray().Single(column => column.GetProperty("name").GetString() == "Amount (USD)");
        Assert.Equal("number", amount.GetProperty("type").GetString());
        Assert.Equal("USD", amount.GetProperty("currency").GetString());
        Assert.Equal("G", amount.GetProperty("letter").GetString());
        var counts = json.GetProperty("rowCounts");
        Assert.Equal(4, counts.GetProperty("item").GetInt32());
        Assert.Equal(2, counts.GetProperty("subtotal").GetInt32());
        Assert.Equal(1, counts.GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("sectionHeader").GetInt32());
    }

    [Fact]
    public void QueryTable_ReturnsExactMatchCountAndEvidenceIds()
    {
        var result = Run("query_table", """
            { "tableId": "T1", "filters": [{ "column": "F", "operator": "gte", "value": "15" }], "rowKinds": ["item"],
              "columns": ["Description", "Amount (USD)"], "sortBy": "Amount (USD)", "sortDescending": true, "limit": 2 }
            """);
        var json = TestSessions.Success(result);

        Assert.Equal(3, json.GetProperty("totalMatches").GetInt32());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        var first = json.GetProperty("rows")[0];
        Assert.Equal(11, first.GetProperty("rowNumber").GetInt32());
        Assert.Equal(10000m, first.GetProperty("values").GetProperty("Amount (USD)").GetDecimal());
        Assert.False(first.GetProperty("values").TryGetProperty("Qty", out _));
        Assert.StartsWith("E", first.GetProperty("evidenceId").GetString());
        Assert.Equal(2, result.EvidenceIds.Count);
    }

    [Fact]
    public void AggregateTable_SumsItemsOnlyByDefaultAndCitesRows()
    {
        var result = Run("aggregate_table", """{ "tableId": "T1", "column": "Amount (USD)", "operation": "sum" }""");
        var json = TestSessions.Success(result);

        Assert.Equal(18000m, json.GetProperty("result").GetDecimal());
        Assert.Equal("USD", json.GetProperty("currency").GetString());
        Assert.Equal(4, json.GetProperty("valuesUsed").GetInt32());
        Assert.Equal(["item"], json.GetProperty("rowKinds").EnumerateArray().Select(kind => kind.GetString()));
        Assert.Equal(4, json.GetProperty("evidenceIds").GetArrayLength());
        Assert.Equal(4, result.EvidenceIds.Count);
    }

    [Fact]
    public void AggregateTable_MixedCurrencies_ReturnsError()
    {
        var session = TestSessions.Create(TestSessions.Workbook(ExcelFixtures.Xlsx(book =>
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
        })));

        var error = TestSessions.Error(_registry.Execute(
            session, "aggregate_table", TestSessions.Json("""{ "tableId": "T1", "column": "Price", "operation": "sum" }"""), CancellationToken.None));

        Assert.Contains("different currencies", error);
    }

    [Fact]
    public void FindDates_ScansAllEvidenceInDateOrder()
    {
        var json = TestSessions.Success(Run("find_dates", "{}"));

        Assert.Equal(
            ["2027-03-15", "2027-06-30", "2027-06-30"],
            json.GetProperty("dates").EnumerateArray().Select(date => date.GetProperty("date").GetString()));
        Assert.Equal(3, json.GetProperty("totalFound").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void FindDates_RangeAndKeywordFilters_Apply()
    {
        var json = TestSessions.Success(Run("find_dates", """{ "from": "2027-06-01", "keyword": "completion" }"""));

        var date = Assert.Single(json.GetProperty("dates").EnumerateArray());
        Assert.Equal("2027-06-30", date.GetProperty("date").GetString());
        Assert.Equal("E2", date.GetProperty("evidenceId").GetString());
    }

    private ToolResult Run(string tool, string input) =>
        _registry.Execute(_session, tool, TestSessions.Json(input), CancellationToken.None);
}
