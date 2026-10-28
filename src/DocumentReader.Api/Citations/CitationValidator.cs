using System.Text.RegularExpressions;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Citations;

/// <summary>The Citation type from docs/api-contract.md, built only from backend-owned evidence metadata.</summary>
public sealed record Citation(string EvidenceId, string DocumentId, string FileName, SourceLocation Source, string Snippet);

/// <summary>An answer whose citation markers have been checked, with the citations it keeps.</summary>
public sealed record ValidatedAnswer(string Text, IReadOnlyList<Citation> Citations);

/// <summary>
/// Checks every [E#] marker in a model answer. A marker is kept only if the evidence exists in this
/// session AND was actually returned to the model (by a tool in this question, or cited in an earlier
/// answer); anything else is removed, so the model cannot cite evidence it never saw or invent IDs.
/// Citation details always come from the stored evidence, never from the model.
/// </summary>
public static partial class CitationValidator
{
    public const int SnippetLength = 300;

    public static ValidatedAnswer Validate(string answer, Session session, IReadOnlySet<string> allowedEvidenceIds)
    {
        var citations = new List<Citation>();
        var cited = new HashSet<string>(StringComparer.Ordinal);

        var text = Marker().Replace(answer, match =>
        {
            var evidenceId = match.Groups["id"].Value;
            if (!allowedEvidenceIds.Contains(evidenceId) || !session.Documents.TryGetEvidence(evidenceId, out var evidence))
            {
                return string.Empty;
            }

            if (cited.Add(evidenceId))
            {
                citations.Add(new Citation(evidence.Id, evidence.DocumentId, evidence.FileName, evidence.Source, Snippet(evidence.Text)));
            }

            return match.Value;
        });

        return new ValidatedAnswer(text, citations);
    }

    /// <summary>The evidence IDs cited in a text (used to allow citing them again in later answers).</summary>
    public static IEnumerable<string> CitedIds(string text) =>
        Marker().Matches(text).Select(match => match.Groups["id"].Value);

    private static string Snippet(string text) =>
        text.Length <= SnippetLength ? text : text[..(SnippetLength - 1)] + "…";

    // Spaces directly before a marker belong to the match, so a removed marker takes them with it
    // ("fact [E9]." becomes "fact.").
    [GeneratedRegex(@"[ \t]*\[(?<id>E\d+)\]")]
    private static partial Regex Marker();
}
