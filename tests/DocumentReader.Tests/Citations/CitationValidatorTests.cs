using DocumentReader.Api.Citations;
using DocumentReader.Api.Ingestion;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Citations;

public sealed class CitationValidatorTests
{
    private readonly DocumentReader.Api.Sessions.Session _session = TestSessions.Create(
        TestSessions.TextDocument("contract.pdf", "Payment terms: 30 days.", new string('x', 500)));

    [Fact]
    public void Validate_KeepsRetrievedMarkersAndBuildsCitationsFromStoredEvidence()
    {
        var result = CitationValidator.Validate("Payment is due in 30 days [E1].", _session, Allowed("E1"));

        Assert.Equal("Payment is due in 30 days [E1].", result.Text);
        var citation = Assert.Single(result.Citations);
        Assert.Equal("E1", citation.EvidenceId);
        Assert.Equal("contract.pdf", citation.FileName);
        Assert.Equal(new PdfPageLocation(1), citation.Source);
        Assert.Equal("Payment terms: 30 days.", citation.Snippet);
    }

    [Fact]
    public void Validate_RemovesUnknownIds()
    {
        var result = CitationValidator.Validate("A fact [E99]. Another [E1].", _session, Allowed("E1", "E99"));

        Assert.Equal("A fact. Another [E1].", result.Text);
        Assert.Equal(["E1"], result.Citations.Select(citation => citation.EvidenceId));
    }

    [Fact]
    public void Validate_RemovesExistingEvidenceTheModelNeverRetrieved()
    {
        var result = CitationValidator.Validate("Guessed citation [E2].", _session, Allowed("E1"));

        Assert.Equal("Guessed citation.", result.Text);
        Assert.Empty(result.Citations);
    }

    [Fact]
    public void Validate_RepeatedAndAdjacentMarkers_ProduceOneCitationEach()
    {
        var result = CitationValidator.Validate("One [E1][E2]. Again [E1].", _session, Allowed("E1", "E2"));

        Assert.Equal("One [E1][E2]. Again [E1].", result.Text);
        Assert.Equal(["E1", "E2"], result.Citations.Select(citation => citation.EvidenceId));
    }

    [Fact]
    public void Validate_LongEvidence_SnippetIsBounded()
    {
        var result = CitationValidator.Validate("Long [E2].", _session, Allowed("E2"));

        Assert.Equal(CitationValidator.SnippetLength, Assert.Single(result.Citations).Snippet.Length);
    }

    [Fact]
    public void Validate_EvidenceFromAnotherSession_IsRemoved()
    {
        var other = TestSessions.Create(TestSessions.TextDocument("other.pdf", "Other"));

        var result = CitationValidator.Validate("Claim [E2].", other, Allowed("E2"));

        Assert.Equal("Claim.", result.Text);
    }

    private static HashSet<string> Allowed(params string[] ids) => new(ids, StringComparer.Ordinal);
}
