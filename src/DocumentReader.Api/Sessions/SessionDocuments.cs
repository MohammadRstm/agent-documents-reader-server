using System.Diagnostics.CodeAnalysis;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Search;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// A detected table as stored in a session. <see cref="TableId"/> has the form T&lt;n&gt; and is unique
/// within the session; <see cref="RowEvidenceIds"/> maps sheet row numbers to the evidence ID of that row.
/// </summary>
public sealed record StoredTable(
    string TableId,
    string DocumentId,
    string FileName,
    DetectedTable Table,
    IReadOnlyDictionary<int, string> RowEvidenceIds);

/// <summary>
/// The documents, evidence, tables and search index of one session. All IDs handed out here
/// (E&lt;n&gt;, T&lt;n&gt;) are only resolvable through this object, so every lookup is session-scoped.
/// Thread-safe: concurrent uploads and reads are serialized with a lock, which keeps IDs, the
/// document limit and the search index consistent.
/// </summary>
public sealed class SessionDocuments
{
    private readonly Lock _lock = new();
    private readonly List<DocumentRecord> _documents = [];
    private readonly Dictionary<string, EvidenceItem> _evidence = new(StringComparer.Ordinal);
    private readonly List<EvidenceItem> _evidenceInOrder = [];
    private readonly Dictionary<string, StoredTable> _tables = new(StringComparer.Ordinal);
    private readonly List<StoredTable> _tablesInOrder = [];
    private readonly Bm25Index _searchIndex = new();
    private int _lastEvidenceNumber;
    private int _lastTableNumber;

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

    public bool TryGetDocument(string documentId, [NotNullWhen(true)] out DocumentRecord? document)
    {
        lock (_lock)
        {
            document = _documents.Find(candidate => candidate.Id == documentId);
            return document is not null;
        }
    }

    /// <summary>Adds documents without a cap on failed documents (see the full overload).</summary>
    public bool TryAdd(IReadOnlyList<IngestedDocument> documents, int maxContentDocuments) =>
        TryAdd(documents, maxContentDocuments, int.MaxValue, out _);

    /// <summary>
    /// Adds the documents, gives each chunk the next evidence ID (E1, E2, ...) and each table the
    /// next table ID (T1, T2, ...), and indexes the evidence for search. Adds nothing and returns
    /// false if the session would hold more than <paramref name="maxContentDocuments"/> documents
    /// with content. Failed documents (no content) are stored only while the session holds fewer
    /// than <paramref name="maxFailedDocuments"/> of them; the IDs of failed documents that were not
    /// stored are returned in <paramref name="notStoredDocumentIds"/>.
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
                if (!ingested.Document.HasContent)
                {
                    if (failedDocuments >= maxFailedDocuments)
                    {
                        notStored.Add(ingested.Document.Id);
                        continue;
                    }

                    failedDocuments++;
                }

                AddDocument(ingested);
            }

            notStoredDocumentIds = notStored;
            return true;
        }
    }

    private void AddDocument(IngestedDocument ingested)
    {
        var document = ingested.Document;
        _documents.Add(document);

        // Table N of the document gets the next session-wide table ID.
        var tableIds = ingested.Tables.Select(_ => $"T{++_lastTableNumber}").ToList();
        var rowEvidence = tableIds.Select(_ => new Dictionary<int, string>()).ToList();

        foreach (var chunk in ingested.Chunks)
        {
            var evidenceId = $"E{++_lastEvidenceNumber}";
            var evidence = new EvidenceItem(
                evidenceId, document.Id, document.FileName, chunk.Kind, chunk.Source, chunk.Text, chunk.TableRow);
            _evidence[evidenceId] = evidence;
            _evidenceInOrder.Add(evidence);
            _searchIndex.Add(evidenceId, chunk.Text);

            if (chunk.TableRow is { } reference && reference.TableNumber >= 1 && reference.TableNumber <= tableIds.Count)
            {
                rowEvidence[reference.TableNumber - 1][reference.RowNumber] = evidenceId;
            }
        }

        for (var index = 0; index < tableIds.Count; index++)
        {
            var stored = new StoredTable(tableIds[index], document.Id, document.FileName, ingested.Tables[index], rowEvidence[index]);
            _tables[stored.TableId] = stored;
            _tablesInOrder.Add(stored);
        }
    }

    /// <summary>All tables in the session, in upload order.</summary>
    public IReadOnlyList<StoredTable> GetTables()
    {
        lock (_lock)
        {
            return [.. _tablesInOrder];
        }
    }

    /// <summary>Looks up a table by ID within this session only.</summary>
    public bool TryGetTable(string tableId, [NotNullWhen(true)] out StoredTable? table)
    {
        lock (_lock)
        {
            return _tables.TryGetValue(tableId, out table);
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

    /// <summary>All evidence in the session in ID order, for exhaustive scans.</summary>
    public IReadOnlyList<EvidenceItem> GetAllEvidence()
    {
        lock (_lock)
        {
            return [.. _evidenceInOrder];
        }
    }

    /// <summary>
    /// Keyword search (BM25) over this session's evidence, optionally limited to some documents.
    /// Returns the best matches only.
    /// </summary>
    public IReadOnlyList<(EvidenceItem Evidence, double Score)> Search(
        string query,
        int limit,
        IReadOnlySet<string>? documentIds = null)
    {
        lock (_lock)
        {
            Func<string, bool>? include = documentIds is null
                ? null
                : evidenceId => documentIds.Contains(_evidence[evidenceId].DocumentId);
            return _searchIndex.Search(query, limit, include)
                .Select(hit => (_evidence[hit.EvidenceId], hit.Score))
                .ToList();
        }
    }
}
