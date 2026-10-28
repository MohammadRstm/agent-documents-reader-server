using System.Text.Json;
using DocumentReader.Api.Search;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Keyword (BM25) search over the session's evidence; returns the best matches with short snippets.</summary>
public sealed class SearchTextTool : IAgentTool
{
    public const int SnippetLength = 300;
    private const int DefaultLimit = 8;
    private const int MaxLimit = 20;

    public string Name => "search_text";

    public string Description =>
        "Keyword search over all extracted text and table rows. Returns the best-matching evidence (evidenceId, source, snippet). " +
        "Results are the top matches only and are NOT exhaustive: for totals, filters or 'all items that...' questions use " +
        "query_table, aggregate_table or find_dates instead.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "minLength": 1, "maxLength": 500, "description": "Keywords to search for." },
            "documentIds": { "type": "array", "items": { "type": "string" }, "maxItems": 20, "description": "Optional: only search these documents." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 20, "description": "Maximum results (default 8)." }
          },
          "required": ["query"],
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "query", "documentIds", "limit");
        var query = arguments.RequiredString("query", 500);
        var documentIds = DocumentIdArguments.Resolve(session, arguments);
        var limit = arguments.OptionalInt("limit", 1, MaxLimit, DefaultLimit);

        var hits = session.Documents.Search(query, limit, documentIds);
        var terms = TextTokenizer.Tokenize(query);
        var results = hits.Select(hit => new
        {
            evidenceId = hit.Evidence.Id,
            documentId = hit.Evidence.DocumentId,
            fileName = hit.Evidence.FileName,
            source = hit.Evidence.Source,
            snippet = Snippet.Around(hit.Evidence.Text, terms, SnippetLength),
            score = Math.Round(hit.Score, 3),
        }).ToList();

        return ToolResult.Success(
            new
            {
                query,
                resultCount = results.Count,
                results,
                note = "Top keyword matches only; not a complete list.",
            },
            results.Select(result => result.evidenceId));
    }
}

/// <summary>Validates optional documentIds arguments against the session.</summary>
public static class DocumentIdArguments
{
    public static IReadOnlySet<string>? Resolve(Session session, ToolArguments arguments)
    {
        var ids = arguments.OptionalStringArray("documentIds", 20, 64);
        if (ids is null)
        {
            return null;
        }

        var unknown = ids.Where(id => !ToolArguments.IsDocumentId(id) || !session.Documents.TryGetDocument(id, out _)).ToList();
        if (unknown.Count > 0)
        {
            throw new ToolArgumentException(
                $"Unknown document ID(s): {string.Join(", ", unknown.Take(5))}. Use list_documents to see document IDs.");
        }

        return ids.ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>Cuts a short excerpt around the first occurrence of any search term.</summary>
public static class Snippet
{
    public static string Around(string text, IReadOnlyList<string> terms, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var position = terms
            .Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Clamp(position - (maxLength / 3), 0, text.Length - maxLength);
        var excerpt = text.Substring(start, maxLength);

        // Mark cut ends with an ellipsis while keeping the total within maxLength.
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + maxLength < text.Length ? "…" : string.Empty;
        return prefix + excerpt[prefix.Length..(excerpt.Length - suffix.Length)] + suffix;
    }
}
