using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Tests.Sessions;

public sealed class SessionDocumentsTests
{
    private readonly SessionDocuments _documents = new();

    [Fact]
    public void TryAdd_AssignsSequentialEvidenceIdsAcrossUploads()
    {
        Assert.True(_documents.TryAdd([Document("doc-a", pages: [1, 2])], maxDocuments: 10));
        Assert.True(_documents.TryAdd([Document("doc-b", pages: [1])], maxDocuments: 10));

        AssertEvidence("E1", "doc-a", page: 1);
        AssertEvidence("E2", "doc-a", page: 2);
        AssertEvidence("E3", "doc-b", page: 1);
        Assert.False(_documents.TryGetEvidence("E4", out _));
    }

    [Fact]
    public void TryAdd_KeepsUploadOrder()
    {
        _documents.TryAdd([Document("first", pages: []), Document("second", pages: [])], maxDocuments: 10);
        _documents.TryAdd([Document("third", pages: [])], maxDocuments: 10);

        Assert.Equal(["first", "second", "third"], _documents.GetDocuments().Select(document => document.Id));
    }

    [Fact]
    public void TryAdd_OverDocumentLimit_AddsNothing()
    {
        _documents.TryAdd([Document("existing", pages: [1])], maxDocuments: 2);

        var added = _documents.TryAdd([Document("new-1", pages: [1]), Document("new-2", pages: [1])], maxDocuments: 2);

        Assert.False(added);
        Assert.Equal(1, _documents.Count);
        Assert.False(_documents.TryGetEvidence("E2", out _));
    }

    [Theory]
    [InlineData("E0")]
    [InlineData("e1")]
    [InlineData("1")]
    [InlineData("")]
    public void TryGetEvidence_UnknownOrMalformedId_ReturnsFalse(string evidenceId)
    {
        _documents.TryAdd([Document("doc", pages: [1])], maxDocuments: 10);

        Assert.False(_documents.TryGetEvidence(evidenceId, out _));
    }

    private void AssertEvidence(string evidenceId, string documentId, int page)
    {
        Assert.True(_documents.TryGetEvidence(evidenceId, out var evidence));
        Assert.Equal(evidenceId, evidence.Id);
        Assert.Equal(documentId, evidence.DocumentId);
        Assert.Equal($"{documentId}.pdf", evidence.FileName);
        Assert.Equal(new PdfPageLocation(page), evidence.Source);
    }

    private static IngestedDocument Document(string id, int[] pages)
    {
        var chunks = pages
            .Select(page => new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(page), $"Text of page {page}"))
            .ToList();
        var record = new DocumentRecord(
            id, $"{id}.pdf", FileType.Pdf, 100, DocumentStatus.Indexed, DateTimeOffset.UnixEpoch, pages.Length, null, 0, chunks.Count, []);
        return new IngestedDocument(record, chunks, []);
    }
}
