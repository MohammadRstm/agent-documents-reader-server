using DocumentReader.Api.Agent;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// The chat history of one session and the "one question at a time" guard. Thread-safe.
/// </summary>
public sealed class SessionConversation
{
    private readonly Lock _lock = new();
    private readonly List<ChatMessage> _messages = [];
    private int _questionInProgress;

    /// <summary>Claims the session for one question. Returns null if a question is already being answered.</summary>
    public IDisposable? TryBeginQuestion() =>
        Interlocked.CompareExchange(ref _questionInProgress, 1, 0) == 0 ? new QuestionLease(this) : null;

    /// <summary>All messages, oldest first.</summary>
    public IReadOnlyList<ChatMessage> GetMessages()
    {
        lock (_lock)
        {
            return [.. _messages];
        }
    }

    /// <summary>Adds a completed question and its answer together.</summary>
    public void AddExchange(ChatMessage question, ChatMessage answer)
    {
        lock (_lock)
        {
            _messages.Add(question);
            _messages.Add(answer);
        }
    }

    private sealed class QuestionLease(SessionConversation conversation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Exchange(ref conversation._questionInProgress, 0);
            }
        }
    }
}
