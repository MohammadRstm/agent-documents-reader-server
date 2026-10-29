using DocumentReader.Api.Citations;

namespace DocumentReader.Api.Agent;

public enum ChatRole
{
    User,
    Assistant,
}

/// <summary>The ChatMessage type from docs/api-contract.md.</summary>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Content,
    DateTime CreatedAt,
    IReadOnlyList<Citation> Citations,
    bool Incomplete);

public sealed record ChatHistoryResponse(IReadOnlyList<ChatMessage> Messages);
