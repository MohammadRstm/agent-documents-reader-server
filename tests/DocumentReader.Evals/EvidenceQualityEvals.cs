using DocumentReader.Tests.Infrastructure;
using static DocumentReader.Evals.EvalHelpers;

namespace DocumentReader.Evals;

/// <summary>Dates, conflicting statements and missing values: the answer reflects exactly what the documents say.</summary>
public sealed class EvidenceQualityEvals(LiveApiFactory factory) : IClassFixture<LiveApiFactory>
{
    [LiveFact]
    public async Task DateFilter_ListsOnlyDeliverablesDueBeforeTheDate()
    {
        var client = await StartAsync(factory, ("deliverables.xlsx", EvalDocuments.Deliverables()));

        var result = await client.AskAsync("Which deliverables are due before 1 May 2027? Give only those, with their due dates.");

        Assert.Contains("Design report", result.Answer);
        Assert.Contains("Site survey", result.Answer);
        Assert.Contains("Prototype", result.Answer);
        Assert.DoesNotContain("Training", result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task EarliestAndLatestDeadline_AreIdentified()
    {
        var client = await StartAsync(factory, ("deliverables.xlsx", EvalDocuments.Deliverables()));

        var result = await client.AskAsync("Which deliverable is due first and which is due last?");

        Assert.Contains("Design report", result.Answer);
        Assert.Contains("Final report", result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task ConflictingStatements_AreBothReported()
    {
        var client = await StartAsync(factory, (
            "contract.pdf",
            PdfFixtures.WithPageTexts(
                "Clause 4.1: The warranty period is 12 months from handover.",
                "Clause 9.3: Notwithstanding other clauses, the warranty period is 24 months from handover.")));

        var result = await client.AskAsync("How long is the warranty period?");

        Assert.Contains("12", result.Answer);
        Assert.Contains("24", result.Answer);
        AssertGrounded(result);
        Assert.Contains(result.Citations, citation => citation.Page == 1);
        Assert.Contains(result.Citations, citation => citation.Page == 2);
    }

    [LiveFact]
    public async Task ConflictAcrossDocuments_IsReported()
    {
        var client = await StartAsync(
            factory,
            ("contract.pdf", PdfFixtures.WithPageTexts("Completion date: 30 June 2027.")),
            ("programme.pdf", PdfFixtures.WithPageTexts("Programme: practical completion planned for 31 August 2027.")));

        var result = await client.AskAsync("When is completion due? Check both documents.");

        Assert.True(ContainsAny(result.Answer, "30 June 2027", "June 30, 2027", "2027-06-30"), result.Answer);
        Assert.True(ContainsAny(result.Answer, "31 August 2027", "August 31, 2027", "2027-08-31"), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task MissingValue_IsNotInvented()
    {
        var client = await StartAsync(factory, ("boq.xlsx", EvalDocuments.BoqWithMissingRate()));

        var result = await client.AskAsync("What is the rate and amount for Waterproofing?");

        Assert.True(ContainsAny(result.Answer, "blank", "empty", "missing", "no rate", "not given", "not provided", "not stated", "not specified", "does not", "doesn't"), result.Answer);
        Assert.False(ContainsAny(result.Answer, "1,600", "1600"), result.Answer);
    }

    [LiveFact]
    public async Task TotalWithMissingValue_SaysWhichItemIsExcluded()
    {
        var client = await StartAsync(factory, ("boq.xlsx", EvalDocuments.BoqWithMissingRate()));

        var result = await client.AskAsync("What is the total amount of the BOQ?");

        Assert.True(ContainsNumber(result.Answer, 4200), result.Answer);
        Assert.Contains("Waterproofing", result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task QuestionOutsideTheDocuments_IsNotAnsweredFromGeneralKnowledge()
    {
        var client = await StartAsync(factory, ("boq.xlsx", ExcelFixtures.BillOfQuantities()));

        var result = await client.AskAsync("What is the current market price of concrete per m3 in London?");

        AssertSaysUnavailable(result.Answer);
    }
}
