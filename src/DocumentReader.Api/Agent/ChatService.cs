using DocumentReader.Api.Citations;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Llm;
using DocumentReader.Api.Sessions;

namespace DocumentReader.Api.Agent;

/// <summary>
/// Runs one chat question end to end: pre-stream checks, the agent loop, citation validation,
/// history, and the final SSE events. Errors after the stream has started are reported as an
/// "error" event with a safe message, as docs/api-contract.md requires.
/// </summary>
public sealed class ChatService(
    AgentRunner agentRunner,
    IChatModelClient model,
    TimeProvider timeProvider,
    ILogger<ChatService> logger)
{
    /// <summary>Earlier question/answer pairs sent to the model with each new question.</summary>
    public const int HistoryPairsSentToModel = 10;

    /// <summary>
    /// Checks that a question can start and claims the session for it. Throws <see cref="ApiException"/>
    /// before any stream is opened, in this order: 502 provider_unavailable, 409 documents_processing,
    /// 409 no_documents, 409 question_in_progress.
    /// </summary>
    public IDisposable BeginQuestion(Session session)
    {
        if (!model.IsConfigured)
        {
            throw new ApiException(
                StatusCodes.Status502BadGateway,
                ErrorCodes.ProviderUnavailable,
                "The AI provider is not configured on the server, so questions cannot be answered right now.");
        }

        if (session.Uploads.InProgress > 0)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ErrorCodes.DocumentsProcessing,
                "Documents are still being processed. Wait until processing finishes, then ask again.");
        }

        if (session.Documents.ContentDocumentCount == 0)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ErrorCodes.NoDocuments,
                "Upload at least one readable PDF or Excel file before asking a question.");
        }

        return session.Conversation.TryBeginQuestion() ?? throw new ApiException(
            StatusCodes.Status409Conflict,
            ErrorCodes.QuestionInProgress,
            "A question is already being answered in this session. Wait for it to finish.");
    }

    /// <summary>Answers the question, writing status, delta, citations and done (or error) events.</summary>
    public async Task StreamAnswerAsync(
        Session session,
        string question,
        ServerSentEventWriter events,
        string traceId,
        CancellationToken cancellationToken)
    {
        try
        {
            var askedAt = timeProvider.GetUtcNow().UtcDateTime;
            var history = session.Conversation.GetMessages();
            var answer = await agentRunner.AnswerAsync(session, ToModelHistory(history), question, events, cancellationToken);

            // Evidence the model may cite: what tools returned now, plus what earlier answers cited.
            var allowed = new HashSet<string>(answer.RetrievedEvidenceIds, StringComparer.Ordinal);
            allowed.UnionWith(history.SelectMany(message => message.Citations.Select(citation => citation.EvidenceId)));
            var validated = CitationValidator.Validate(answer.Text, session, allowed);

            var userMessage = new ChatMessage(NewId(), ChatRole.User, question, askedAt, [], Incomplete: false);
            var assistantMessage = new ChatMessage(
                NewId(), ChatRole.Assistant, validated.Text, timeProvider.GetUtcNow().UtcDateTime, validated.Citations, answer.Incomplete);
            session.Conversation.AddExchange(userMessage, assistantMessage);

            await events.WriteAsync("citations", new { citations = validated.Citations }, cancellationToken);
            await events.WriteAsync(
                "done",
                new { messageId = assistantMessage.Id, answer = assistantMessage.Content, incomplete = assistantMessage.Incomplete },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client disconnected; nothing more can be sent.
            logger.LogInformation("Chat request was cancelled by the client. TraceId: {TraceId}", traceId);
        }
        catch (ChatModelUnavailableException exception)
        {
            logger.LogWarning(exception, "Chat failed because the AI provider was unavailable. TraceId: {TraceId}", traceId);
            await WriteErrorAsync(events, ErrorCodes.ProviderUnavailable, "The AI provider is unavailable. Please try again.", traceId, cancellationToken);
        }
        catch (AgentTimeoutException)
        {
            logger.LogWarning("Chat answer timed out. TraceId: {TraceId}", traceId);
            await WriteErrorAsync(events, ErrorCodes.ProviderUnavailable, "The answer took too long. Please try again or ask a narrower question.", traceId, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Chat failed unexpectedly. TraceId: {TraceId}", traceId);
            await WriteErrorAsync(events, ErrorCodes.InternalError, "An unexpected error occurred.", traceId, cancellationToken);
        }
    }

    /// <summary>The last question/answer pairs as plain text; earlier reasoning and tool calls are not replayed.</summary>
    private static List<ModelTurn> ToModelHistory(IReadOnlyList<ChatMessage> history) =>
        history
            .TakeLast(HistoryPairsSentToModel * 2)
            .SkipWhile(message => message.Role != ChatRole.User)
            .Select(message => message.Role == ChatRole.User
                ? (ModelTurn)new UserTextTurn(message.Content)
                : new AssistantTextTurn(message.Content))
            .ToList();

    private static async Task WriteErrorAsync(
        ServerSentEventWriter events,
        string code,
        string message,
        string traceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await events.WriteAsync("error", new { code, message, traceId }, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The client is gone; there is nobody to tell.
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
