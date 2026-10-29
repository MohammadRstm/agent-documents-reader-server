using System.Diagnostics.CodeAnalysis;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Ingestion.Excel;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// The documents and evidence of one session. Thread-safe: concurrent uploads to the same
/// session are serialized when they are added, so evidence IDs and the document limit stay consistent.
/// </summary>
public sealed class SessionDocuments
{
    private readonly Lock _lock = new();
    private readonly List<DocumentRecord> _documents = [];
    private readonly Dictionary<string, EvidenceItem> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<DetectedTable>> _tables = new(StringComparer.Ordinal);
    private int _lastEvidenceNumber;

    /// <summary>
    /// Documents that produced content (indexed or partially indexed). Only these count toward the
    /// per-session document limit; failed files are kept and listed but do not use up the limit.
    /// </summary>
    public int ContentDocumentCount
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count(document => document.HasContent);
            }
        }
    }

    /// <summary>All documents in upload order.</summary>
    public IReadOnlyList<DocumentRecord> GetDocuments()
    {
        lock (_lock)
        {
            return [.. _documents];
        }
    }

    /// <summary>Adds documents without a cap on failed documents (see the full overload).</summary>
    public bool TryAdd(IReadOnlyList<IngestedDocument> documents, int maxContentDocuments) =>
        TryAdd(documents, maxContentDocuments, int.MaxValue, out _);

    /// <summary>
    /// Adds the documents and gives each chunk the next evidence ID (E1, E2, ...). Adds nothing and
    /// returns false if the session would hold more than <paramref name="maxContentDocuments"/>
    /// documents with content. Failed documents (no content) are stored only while the session holds
    /// fewer than <paramref name="maxFailedDocuments"/> of them; the IDs of failed documents that were
    /// not stored are returned in <paramref name="notStoredDocumentIds"/>.
    /// </summary>
    public bool TryAdd(
        IReadOnlyList<IngestedDocument> documents,
        int maxContentDocuments,
        int maxFailedDocuments,
        out IReadOnlyList<string> notStoredDocumentIds)
    {
        lock (_lock)
        {
            notStoredDocumentIds = [];
            var contentDocuments = _documents.Count(document => document.HasContent)
                + documents.Count(ingested => ingested.Document.HasContent);
            if (contentDocuments > maxContentDocuments)
            {
                return false;
            }

            var failedDocuments = _documents.Count(document => !document.HasContent);
            var notStored = new List<string>();
            foreach (var ingested in documents)
            {
                var document = ingested.Document;
                if (!document.HasContent)
                {
                    if (failedDocuments >= maxFailedDocuments)
                    {
                        notStored.Add(document.Id);
                        continue;
                    }

                    failedDocuments++;
                }

                _documents.Add(document);
                _tables[document.Id] = ingested.Tables;
                foreach (var chunk in ingested.Chunks)
                {
                    _lastEvidenceNumber++;
                    var evidenceId = $"E{_lastEvidenceNumber}";
                    _evidence[evidenceId] = new EvidenceItem(
                        evidenceId, document.Id, document.FileName, chunk.Kind, chunk.Source, chunk.Text, chunk.TableRow);
                }
            }

            notStoredDocumentIds = notStored;
            return true;
        }
    }

    /// <summary>
    /// Detected tables of a document in this session; table N is at index N-1. Empty when the
    /// document is not in this session or has no tables.
    /// </summary>
    public IReadOnlyList<DetectedTable> GetTables(string documentId)
    {
        lock (_lock)
        {
            return _tables.TryGetValue(documentId, out var tables) ? tables : [];
        }
    }

    /// <summary>Looks up evidence by ID within this session only.</summary>
    public bool TryGetEvidence(string evidenceId, [NotNullWhen(true)] out EvidenceItem? evidence)
    {
        lock (_lock)
        {
            return _evidence.TryGetValue(evidenceId, out evidence);
        }
    }
}
