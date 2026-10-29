namespace DocumentReader.Api.Ingestion;

/// <summary>The EvidenceDetail response type from docs/api-contract.md.</summary>
public sealed record EvidenceDetail(
    string EvidenceId,
    string DocumentId,
    string FileName,
    EvidenceKind Kind,
    SourceLocation Source,
    string Text)
{
    /// <summary>Contract limit for <see cref="Text"/>.</summary>
    public const int MaxTextLength = 4000;

    public static EvidenceDetail From(EvidenceItem evidence) => new(
        evidence.Id,
        evidence.DocumentId,
        evidence.FileName,
        evidence.Kind,
        evidence.Source,
        evidence.Text.Length <= MaxTextLength ? evidence.Text : evidence.Text[..MaxTextLength]);
}
