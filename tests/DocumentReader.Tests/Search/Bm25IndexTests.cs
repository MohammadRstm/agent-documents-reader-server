using DocumentReader.Api.Search;

namespace DocumentReader.Tests.Search;

public sealed class Bm25IndexTests
{
    [Fact]
    public void Search_RanksDocumentWithMoreMatchingTermsFirst()
    {
        var index = new Bm25Index();
        index.Add("E1", "General conditions of contract");
        index.Add("E2", "Payment terms: payment is due within 30 days of invoice");
        index.Add("E3", "Delivery schedule and payment milestones");

        var hits = index.Search("payment terms", limit: 10);

        Assert.Equal(["E2", "E3"], hits.Select(hit => hit.EvidenceId));
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [Fact]
    public void Search_IsCaseInsensitiveAndIgnoresPunctuation()
    {
        var index = new Bm25Index();
        index.Add("E1", "EXCAVATION, m3");

        Assert.Single(index.Search("excavation", limit: 5));
    }

    [Fact]
    public void Search_EqualScores_AreOrderedByEvidenceNumber()
    {
        var index = new Bm25Index();
        index.Add("E10", "concrete");
        index.Add("E2", "concrete");
        index.Add("E1", "concrete");

        Assert.Equal(["E1", "E2", "E10"], index.Search("concrete", limit: 5).Select(hit => hit.EvidenceId));
    }

    [Fact]
    public void Search_RespectsLimitAndFilter()
    {
        var index = new Bm25Index();
        index.Add("E1", "steel");
        index.Add("E2", "steel");
        index.Add("E3", "steel");

        Assert.Single(index.Search("steel", limit: 1));
        Assert.Equal(["E3"], index.Search("steel", limit: 5, include: id => id == "E3").Select(hit => hit.EvidenceId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("the and of")]
    [InlineData("nonexistent")]
    public void Search_WithoutMatchingTerms_ReturnsNothing(string query)
    {
        var index = new Bm25Index();
        index.Add("E1", "The scope of the works");

        Assert.Empty(index.Search(query, limit: 5));
    }

    [Fact]
    public void Add_SameEvidenceTwice_Throws()
    {
        var index = new Bm25Index();
        index.Add("E1", "text");

        Assert.Throws<InvalidOperationException>(() => index.Add("E1", "text"));
    }

    [Fact]
    public void Tokenize_KeepsNumbersAndNonLatinWords()
    {
        Assert.Equal(["item", "1", "1", "excavation", "حفر"], TextTokenizer.Tokenize("Item 1.1 - Excavation (حفر)"));
    }
}
