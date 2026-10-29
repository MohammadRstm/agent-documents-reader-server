using DocumentReader.Tests.Infrastructure;
using static DocumentReader.Evals.EvalHelpers;

namespace DocumentReader.Evals;

/// <summary>
/// Scanned, empty, partial, damaged and unsupported files: the model must say what could not be read
/// instead of inventing content, and still answer from the files that were read.
/// </summary>
public sealed class DocumentProblemEvals(LiveApiFactory factory) : IClassFixture<LiveApiFactory>
{
    [LiveFact]
    public async Task ScannedPdf_IsReportedAsUnreadable()
    {
        var client = await StartAsync(
            factory,
            new Dictionary<string, string> { ["scan.pdf"] = "noTextExtracted" },
            ("scan.pdf", PdfFixtures.ImageOnly(pageCount: 2)));

        var result = await client.AskAsync("What does scan.pdf say about the payment terms?");

        Assert.True(ContainsAny(result.Answer, "scan", "image", "no text", "no readable", "could not", "couldn't", "OCR"), result.Answer);
        AssertSaysUnavailable(result.Answer);
    }

    [LiveFact]
    public async Task PartiallyScannedPdf_AnswersFromTextAndMentionsTheUnreadablePage()
    {
        var client = await StartAsync(
            factory,
            new Dictionary<string, string> { ["scope.pdf"] = "partiallyIndexed" },
            ("scope.pdf", PdfFixtures.TextThenImageOnlyPage("Scope of work: site survey of plot 14.")));

        var result = await client.AskAsync("Summarise scope.pdf. Is anything in it unreadable?");

        Assert.Contains("survey", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.True(ContainsAny(result.Answer, "page 2", "second page", "scanned", "image", "no text", "could not be read"), result.Answer);
        AssertGrounded(result);
    }

    [LiveFact]
    public async Task EmptyPdf_IsReportedAsEmpty()
    {
        var client = await StartAsync(
            factory,
            new Dictionary<string, string> { ["blank.pdf"] = "empty" },
            ("blank.pdf", PdfFixtures.Blank(pageCount: 2)));

        var result = await client.AskAsync("What is the contract value in blank.pdf?");

        Assert.True(ContainsAny(result.Answer, "empty", "no content", "no text", "blank", "nothing"), result.Answer);
        Assert.Empty(result.Citations);
    }

    [LiveFact]
    public async Task DamagedFiles_AreReportedAndGoodFilesStillAnswer()
    {
        var client = await StartAsync(
            factory,
            new Dictionary<string, string>
            {
                ["damaged.pdf"] = "corrupt",
                ["broken.xlsx"] = "corrupt",
                ["photo.png"] = "unsupported",
            },
            ("damaged.pdf", PdfFixtures.Corrupt()),
            ("broken.xlsx", ExcelFixtures.TruncatedXlsx()),
            ("photo.png", ExcelFixtures.Png()),
            ("contract.pdf", PdfFixtures.WithPageTexts("Payment terms: 45 days from invoice.")));

        var result = await client.AskAsync("Which of my files could not be read, and what are the payment terms?");

        Assert.Contains("damaged.pdf", result.Answer);
        Assert.Contains("broken.xlsx", result.Answer);
        Assert.Contains("photo.png", result.Answer);
        Assert.DoesNotContain("contract.pdf could not", result.Answer);
        Assert.Contains("45", result.Answer);
        AssertGrounded(result);
        Assert.All(result.Citations, citation => Assert.Equal("contract.pdf", citation.FileName));
    }

    [LiveFact]
    public async Task QuestionOnlyAboutADamagedFile_DoesNotInventItsContent()
    {
        var client = await StartAsync(
            factory,
            new Dictionary<string, string> { ["budget.xlsx"] = "corrupt" },
            ("budget.xlsx", ExcelFixtures.TruncatedXlsx()),
            ("notes.pdf", PdfFixtures.WithPageTexts("Kick-off meeting held on 3 May 2027.")));

        var result = await client.AskAsync("What is the total budget in budget.xlsx?");

        AssertSaysUnavailable(result.Answer);
        Assert.True(ContainsAny(result.Answer, "corrupt", "damaged", "could not be read", "couldn't be read", "unreadable", "not readable"), result.Answer);
        Assert.DoesNotContain(result.Citations, citation => citation.FileName == "budget.xlsx");
    }
}
