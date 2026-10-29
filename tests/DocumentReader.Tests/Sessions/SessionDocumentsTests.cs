using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Tests.Sessions;

public sealed class SessionDocumentsTests
{
    private readonly SessionDocuments _documents = new();

    [Fact]
    public void TryAdd_AssignsSequentialEvidenceIdsAcrossUploads()
    {
        Assert.True(_documents.TryAdd([Document("doc-a", pages: [1, 2])], maxContentDocuments: 10));
        Assert.True(_documents.TryAdd([Document("doc-b", pages: [1])], maxContentDocuments: 10));

        AssertEvidence("E1", "doc-a", page: 1);
        AssertEvidence("E2", "doc-a", page: 2);
        AssertEvidence("E3", "doc-b", page: 1);
        Assert.False(_documents.TryGetEvidence("E4", out _));
    }

    [Fact]
    public void TryAdd_KeepsUploadOrder()
    {
        _documents.TryAdd([Document("first", pages: []), Document("second", pages: [])], maxContentDocuments: 10);
        _documents.TryAdd([Document("third", pages: [])], maxContentDocuments: 10);

        Assert.Equal(["first", "second", "third"], _documents.GetDocuments().Select(document => document.Id));
    }

    [Fact]
    public void TryAdd_OverDocumentLimit_AddsNothing()
    {
        _documents.TryAdd([Document("existing", pages: [1])], maxContentDocuments: 2);

        var added = _documents.TryAdd([Document("new-1", pages: [1]), Document("new-2", pages: [1])], maxContentDocuments: 2);

        Assert.False(added);
        Assert.Equal(1, _documents.ContentDocumentCount);
        Assert.Single(_documents.GetDocuments());
        Assert.False(_documents.TryGetEvidence("E2", out _));
    }

    [Fact]
    public void TryAdd_FailedDocuments_AreStoredButDoNotCountTowardLimit()
    {
        Assert.True(_documents.TryAdd([Document("readable", pages: [1])], maxContentDocuments: 1));

        var addedFailed = _documents.TryAdd(
            [Document("broken", pages: [], DocumentStatus.Corrupt), Document("scan", pages: [], DocumentStatus.NoTextExtracted)],
            maxContentDocuments: 1);

        Assert.True(addedFailed);
        Assert.Equal(3, _documents.GetDocuments().Count);
        Assert.Equal(1, _documents.ContentDocumentCount);
        Assert.False(_documents.TryAdd([Document("second-readable", pages: [1])], maxContentDocuments: 1));
    }

    [Fact]
    public void TryAdd_FailedDocumentsBeyondCap_AreNotStoredButReported()
    {
        Assert.True(_documents.TryAdd([Document("failed-1", pages: [], DocumentStatus.Corrupt)], 10, maxFailedDocuments: 2, out var first));
        Assert.Empty(first);

        var added = _documents.TryAdd(
            [
                Document("failed-2", pages: [], DocumentStatus.Unsupported),
                Document("failed-3", pages: [], DocumentStatus.Empty),
                Document("readable", pages: [1]),
            ],
            10,
            maxFailedDocuments: 2,
            out var notStored);

        Assert.True(added);
        Assert.Equal(["failed-3"], notStored);
        Assert.Equal(["failed-1", "failed-2", "readable"], _documents.GetDocuments().Select(document => document.Id));
    }

    [Fact]
    public void TryAdd_PartiallyIndexedDocument_CountsTowardLimit()
    {
        Assert.True(_documents.TryAdd([Document("partial", pages: [1], DocumentStatus.PartiallyIndexed)], maxContentDocuments: 1));

        Assert.Equal(1, _documents.ContentDocumentCount);
        Assert.False(_documents.TryAdd([Document("another", pages: [1])], maxContentDocuments: 1));
    }

    [Theory]
    [InlineData("E0")]
    [InlineData("e1")]
    [InlineData("1")]
    [InlineData("")]
    public void TryGetEvidence_UnknownOrMalformedId_ReturnsFalse(string evidenceId)
    {
        _documents.TryAdd([Document("doc", pages: [1])], maxContentDocuments: 10);

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

    private static IngestedDocument Document(string id, int[] pages, DocumentStatus status = DocumentStatus.Indexed)
    {
        var chunks = pages
            .Select(page => new ExtractedChunk(EvidenceKind.TextChunk, new PdfPageLocation(page), $"Text of page {page}"))
            .ToList();
        var record = new DocumentRecord(
            id, $"{id}.pdf", FileType.Pdf, 100, status, DateTimeOffset.UnixEpoch, pages.Length, null, 0, chunks.Count, []);
        return new IngestedDocument(record, chunks, []);
    }
}
