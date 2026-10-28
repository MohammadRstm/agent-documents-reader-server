using System.Globalization;

namespace DocumentReader.Api.Search;

/// <summary>A search match: the evidence ID and its BM25 score.</summary>
public sealed record SearchHit(string EvidenceId, double Score);

/// <summary>
/// An in-memory inverted index ranked with Okapi BM25 (k1 = 1.2, b = 0.75). Evidence is added
/// incrementally as documents are uploaded. Not thread-safe: the owning session serializes access.
/// Keyword ranking returns the best matches only; it is never used for questions that need every
/// matching record (those use the deterministic table and date tools).
/// </summary>
public sealed class Bm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentLengths = new(StringComparer.Ordinal);
    private long _totalLength;

    public int Count => _documentLengths.Count;

    public void Add(string evidenceId, string text)
    {
        if (_documentLengths.ContainsKey(evidenceId))
        {
            throw new InvalidOperationException($"Evidence {evidenceId} is already indexed.");
        }

        var tokens = TextTokenizer.Tokenize(text);
        _documentLengths[evidenceId] = tokens.Count;
        _totalLength += tokens.Count;

        foreach (var group in tokens.GroupBy(token => token, StringComparer.Ordinal))
        {
            if (!_postings.TryGetValue(group.Key, out var posting))
            {
                posting = new Dictionary<string, int>(StringComparer.Ordinal);
                _postings[group.Key] = posting;
            }

            posting[evidenceId] = group.Count();
        }
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> hits, best first. Ties are broken by evidence order
    /// so results are deterministic. <paramref name="include"/> optionally restricts the candidates.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit, Func<string, bool>? include = null)
    {
        var terms = TextTokenizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || _documentLengths.Count == 0)
        {
            return [];
        }

        var documentCount = _documentLengths.Count;
        var averageLength = Math.Max(1.0, (double)_totalLength / documentCount);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var posting))
            {
                continue;
            }

            var inverseDocumentFrequency = Math.Log(1 + ((documentCount - posting.Count + 0.5) / (posting.Count + 0.5)));
            foreach (var (evidenceId, termFrequency) in posting)
            {
                if (include is not null && !include(evidenceId))
                {
                    continue;
                }

                var length = _documentLengths[evidenceId];
                var score = inverseDocumentFrequency * termFrequency * (K1 + 1)
                    / (termFrequency + (K1 * (1 - B + (B * length / averageLength))));
                scores[evidenceId] = scores.GetValueOrDefault(evidenceId) + score;
            }
        }

        return scores
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => EvidenceNumber(entry.Key))
            .Take(limit)
            .Select(entry => new SearchHit(entry.Key, entry.Value))
            .ToList();
    }

    private static long EvidenceNumber(string evidenceId) =>
        long.TryParse(evidenceId.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : long.MaxValue;
}
