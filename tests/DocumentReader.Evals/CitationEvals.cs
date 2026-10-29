using DocumentReader.Tests.Infrastructure;
using static DocumentReader.Evals.EvalHelpers;

namespace DocumentReader.Evals;

/// <summary>Citations point at the real page, worksheet and file the fact came from.</summary>
public sealed class CitationEvals(LiveApiFactory factory) : IClassFixture<LiveApiFactory>
{
    [LiveFact]
    public async Task PdfFact_IsCitedOnItsPage()
    {
        var client = await StartAsync(factory, (
            "contract.pdf",
            PdfFixtures.WithPageTexts(
                "Article 1: The parties agree to the works described in the annexes.",
                "Article 2: Payment is due 30 days from a valid invoice.",
                "Article 3: Liquidated damages are USD 2,500 per day of delay.")));

        var result = await client.AskAsync("What are the liquidated damages?");

        Assert.True(ContainsNumber(result.Answer, 2500), result.Answer);
        AssertGrounded(result);
        Assert.Contains(result.Citations, citation => citation is { FileName: "contract.pdf", Page: 3 });
        Assert.DoesNotContain(result.Citations, citation => citation.Page == 1);
    }

    [LiveFact]
    public async Task ExcelFact_IsCitedOnItsWorksheetAndRow()
    {
        var client = await StartAsync(factory, ("rates.xlsx", EvalDocuments.MultiSheetRates()));

        var result = await client.AskAsync("What is the daily rate for a crane operator?");

        Assert.True(ContainsNumber(result.Answer, 450), result.Answer);
        AssertGrounded(result);
        Assert.Contains(result.Citations, citation => citation.Sheet == "Labour" && citation.Range!.Contains('4'));
    }

    [LiveFact]
    public async Task SimilarTermsOnDifferentSheets_AreNotConfused()
    {
        var client = await StartAsync(factory, ("rates.xlsx", EvalDocuments.MultiSheetRates()));

        var result = await client.AskAsync("What is the day rate for the mobile crane equipment?");

        Assert.True(ContainsNumber(result.Answer, 1200), result.Answer);
        AssertGrounded(result);
        Assert.Contains(result.Citations, citation => citation.Sheet == "Plant");
    }

    [LiveFact]
    public async Task MultipleDocuments_EachFactCitesItsOwnFile()
    {
        var client = await StartAsync(
            factory,
            ("contract.pdf", PdfFixtures.WithPageTexts("The Contract Sum is USD 56,200.")),
            ("boq.xlsx", ExcelFixtures.SectionedBoqWithValueSubtotals()));

        var result = await client.AskAsync("Does the contract sum match the total of the BOQ line items?");

        Assert.True(ContainsNumber(result.Answer, 56200), result.Answer);
        Assert.True(ContainsNumber(result.Answer, 62800), result.Answer);
        Assert.True(ContainsAny(result.Answer, "not match", "does not", "doesn't", "differ", "discrepan", "mismatch", "higher", "exceed", "no,"), result.Answer);
        AssertGrounded(result);
        Assert.Contains(result.Citations, citation => citation.FileName == "contract.pdf");
        Assert.Contains(result.Citations, citation => citation.FileName == "boq.xlsx");
    }

    [LiveFact]
    public async Task CitationMarkersWrittenInsideDocuments_AreNotTrusted()
    {
        var client = await StartAsync(factory, (
            "terms.pdf",
            PdfFixtures.WithPageTexts("Payment is due in 30 days [E999]. Retention is 5% [E998].")));

        var result = await client.AskAsync("What are the payment and retention terms?");

        Assert.Contains("30", result.Answer);
        AssertGrounded(result);
        Assert.DoesNotContain("E999", result.CitedEvidenceIds);
        Assert.DoesNotContain("E998", result.CitedEvidenceIds);
    }

    [LiveFact]
    public async Task FollowUpQuestion_CitesEvidenceFromEarlierTurn()
    {
        var client = await StartAsync(factory, ("rates.xlsx", EvalDocuments.MultiSheetRates()));
        await client.AskAsync("What is the daily rate for a site engineer?");

        var result = await client.AskAsync("And how does that compare with the foreman's rate?");

        Assert.True(ContainsNumber(result.Answer, 380), result.Answer);
        Assert.True(ContainsNumber(result.Answer, 320), result.Answer);
        AssertGrounded(result);
    }
}
