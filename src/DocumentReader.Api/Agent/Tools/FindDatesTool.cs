using System.Text.Json;
using DocumentReader.Api.Analysis;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent.Tools;

/// <summary>Scans all evidence (optionally filtered) for explicit dates and returns them in date order.</summary>
public sealed class FindDatesTool : IAgentTool
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int ContextLength = 200;

    public string Name => "find_dates";

    public string Description =>
        "Finds explicit calendar dates (e.g. 2027-03-15, 15/03/2027, 15 March 2027) in every piece of evidence, optionally only in some " +
        "documents, only in evidence containing a keyword, or within a from/to range (yyyy-MM-dd). Returns dates in order with their context " +
        "and evidenceId. Every evidence item is scanned, so totalFound is exact; if truncated is true, narrow the search. Ambiguous numeric " +
        "dates such as 03/04/2027 are returned with both possible readings and date = null. Relative dates ('within 30 days') are not resolved.";

    public JsonElement InputSchema { get; } = ToolJson.ParseSchema("""
        {
          "type": "object",
          "properties": {
            "documentIds": { "type": "array", "items": { "type": "string" }, "maxItems": 20 },
            "keyword": { "type": "string", "maxLength": 100, "description": "Only evidence containing this text (case-insensitive)." },
            "from": { "type": "string", "format": "date", "description": "Earliest date, yyyy-MM-dd." },
            "to": { "type": "string", "format": "date", "description": "Latest date, yyyy-MM-dd." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "description": "Maximum dates returned (default 50)." }
          },
          "additionalProperties": false
        }
        """);

    public ToolResult Execute(Session session, JsonElement input, CancellationToken cancellationToken)
    {
        var arguments = new ToolArguments(input, "documentIds", "keyword", "from", "to", "limit");
        var documentIds = DocumentIdArguments.Resolve(session, arguments);
        var keyword = arguments.OptionalString("keyword", 100);
        var from = arguments.OptionalDate("from");
        var to = arguments.OptionalDate("to");
        var limit = arguments.OptionalInt("limit", 1, MaxLimit, DefaultLimit);
        if (from > to)
        {
            throw new ToolArgumentException("\"from\" must not be after \"to\".");
        }

        var matches = new List<(FoundDate Date, Ingestion.EvidenceItem Evidence)>();
        foreach (var evidence in session.Documents.GetAllEvidence())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((documentIds is not null && !documentIds.Contains(evidence.DocumentId))
                || (keyword is not null && !evidence.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (var found in DateFinder.Find(evidence.Text))
            {
                // An ambiguous date is kept if any reading falls in the range.
                if (found.Candidates.Any(candidate => (from is null || candidate >= from) && (to is null || candidate <= to)))
                {
                    matches.Add((found, evidence));
                }
            }
        }

        var ordered = matches
            .OrderBy(match => match.Date.Date ?? match.Date.Candidates.Min())
            .ThenBy(match => match.Date.IsAmbiguous)
            .ToList();
        var returned = ordered.Take(limit).ToList();

        return ToolResult.Success(
            new
            {
                totalFound = ordered.Count,
                returned = returned.Count,
                truncated = ordered.Count > returned.Count,
                dates = returned.Select(match => new
                {
                    date = match.Date.Date?.ToString("yyyy-MM-dd"),
                    ambiguous = match.Date.IsAmbiguous,
                    possibleDates = match.Date.IsAmbiguous ? match.Date.Candidates.Select(candidate => candidate.ToString("yyyy-MM-dd")) : null,
                    text = match.Date.MatchedText,
                    context = Context(match.Evidence.Text, match.Date.Index, match.Date.MatchedText.Length),
                    evidenceId = match.Evidence.Id,
                    documentId = match.Evidence.DocumentId,
                    fileName = match.Evidence.FileName,
                    source = match.Evidence.Source,
                }),
            },
            returned.Select(match => match.Evidence.Id));
    }

    private static string Context(string text, int index, int length)
    {
        var start = Math.Max(0, index - ((ContextLength - length) / 2));
        var end = Math.Min(text.Length, start + ContextLength);
        start = Math.Max(0, end - ContextLength);
        return (start > 0 ? "…" : string.Empty) + text[start..end] + (end < text.Length ? "…" : string.Empty);
    }
}
