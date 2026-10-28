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

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count;
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

    /// <summary>
    /// Adds the documents and gives each chunk the next evidence ID (E1, E2, ...).
    /// Adds nothing and returns false if the session would exceed <paramref name="maxDocuments"/>.
    /// </summary>
    public bool TryAdd(IReadOnlyList<IngestedDocument> documents, int maxDocuments)
    {
        lock (_lock)
        {
            if (_documents.Count + documents.Count > maxDocuments)
            {
                return false;
            }

            foreach (var ingested in documents)
            {
                var document = ingested.Document;
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
