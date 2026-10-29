using DocumentReader.Tests.Infrastructure;
using static DocumentReader.Evals.EvalHelpers;

namespace DocumentReader.Evals;

/// <summary>Totals, filters, counts and currencies that must be calculated exactly from the whole table.</summary>
public sealed class CalculationEvals(LiveApiFactory factory) : IClassFixture<LiveApiFactory>
{
    [LiveFact]
    public async Task TypedInSubtotals_AreNotCountedTwice()
    {
        var client = await StartAsync(factory, ("boq.xlsx", ExcelFixtures.SectionedBoqWithValueSubtotals()));

        var result = await client.AskAsync("What is the total of all BOQ line items?");

        Assert.True(ContainsNumber(result.Answer, 62800), result.Answer);
        Assert.False(ContainsNumber(result.Answer, 125600), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task SectionFilter_TotalsOnlyThatSection()
    {
        var client = await StartAsync(factory, ("boq.xlsx", ExcelFixtures.SectionedBoqWithValueSubtotals()));

        var result = await client.AskAsync("What is the total of the Section 2 (Concrete) items only?");

        Assert.True(ContainsNumber(result.Answer, 48100), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task UnitFilter_SelectsMatchingItems()
    {
        var client = await StartAsync(factory, ("boq.xlsx", ExcelFixtures.SectionedBoqWithValueSubtotals()));

        var result = await client.AskAsync("Which items are measured in m2, and what is their total amount?");

        Assert.Contains("Slabs", result.Answer);
        Assert.True(ContainsNumber(result.Answer, 13200), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task ItemCount_ExcludesHeadersAndTotals()
    {
        var client = await StartAsync(factory, ("boq.xlsx", ExcelFixtures.SectionedBoqWithValueSubtotals()));

        var result = await client.AskAsync("How many priced line items does the BOQ contain? Do not count section headers, subtotals or totals.");

        Assert.True(ContainsAny(result.Answer, "6 ", "six", "6.", "**6**"), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task MixedCurrencies_AreNeverAddedTogether()
    {
        var client = await StartAsync(factory, ("costs.xlsx", EvalDocuments.MixedCurrencyItems()));

        var result = await client.AskAsync("What is the total cost of all items?");

        Assert.False(ContainsNumber(result.Answer, 3500), result.Answer);
        Assert.True(ContainsNumber(result.Answer, 1500), result.Answer);
        Assert.True(ContainsNumber(result.Answer, 2000), result.Answer);
        Assert.True(ContainsAny(result.Answer, "EUR", "€"), result.Answer);
        Assert.True(ContainsAny(result.Answer, "USD", "$"), result.Answer);
    }

    [LiveFact]
    public async Task LargeTable_FilteredCountAndTotalCoverEveryRow()
    {
        var client = await StartAsync(factory, ("register.xlsx", EvalDocuments.LargeRegister()));
        var electrical = Enumerable.Range(1, EvalDocuments.LargeRegisterRowCount)
            .Where(i => EvalDocuments.RegisterCategory(i) == "Electrical")
            .ToList();

        var result = await client.AskAsync("How many Electrical items are in the register, and what is their total amount?");

        Assert.True(ContainsNumber(result.Answer, electrical.Count), result.Answer);
        Assert.True(ContainsNumber(result.Answer, electrical.Sum(EvalDocuments.RegisterAmount)), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task LargeTable_ListingEveryMatchIsExhaustive()
    {
        var client = await StartAsync(factory, ("register.xlsx", EvalDocuments.LargeRegister()));
        var electricalCodes = Enumerable.Range(1, EvalDocuments.LargeRegisterRowCount)
            .Where(i => EvalDocuments.RegisterCategory(i) == "Electrical")
            .Select(EvalDocuments.RegisterCode)
            .ToList();

        var result = await client.AskAsync("List the code of every Electrical item in the register.");

        var missing = electricalCodes.Where(code => !result.Answer.Contains(code, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, $"Missing codes: {string.Join(", ", missing)}\n{result.Answer}");
        Assert.DoesNotContain("R-001", result.Answer);
    }

    [LiveFact]
    public async Task LargeTable_GrandTotalOfAllRows()
    {
        var client = await StartAsync(factory, ("register.xlsx", EvalDocuments.LargeRegister()));
        var total = Enumerable.Range(1, EvalDocuments.LargeRegisterRowCount).Sum(EvalDocuments.RegisterAmount);

        var result = await client.AskAsync("What is the total amount of all items in the register?");

        Assert.True(ContainsNumber(result.Answer, total), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task LegacyXls_IsReadAndTotalled()
    {
        var client = await StartAsync(factory, ("boq.xls", XlsFixtures.BillOfQuantities()));

        var result = await client.AskAsync("What is the total amount of all BOQ line items, excluding subtotals and the grand total row?");

        Assert.True(ContainsNumber(result.Answer, 18000), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task UnfamiliarLayout_TableBelowNotesIsFoundAndTotalled()
    {
        var client = await StartAsync(factory, ("schedule.xlsx", EvalDocuments.UnfamiliarLayout()));

        var result = await client.AskAsync("What is the total value of Schedule B?");

        Assert.True(ContainsNumber(result.Answer, 13000), result.Answer);
        Assert.True(ContainsAny(result.Answer, "EUR", "€"), result.Answer);
        AssertGrounded(result);
    }
}
