using DocumentReader.Api.Agent;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Sessions;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController(ChatService chatService, IOptions<HttpJsonOptions> jsonOptions) : ControllerBase
{
    /// <summary>
    /// Asks a question about the session's documents. Request errors are normal JSON error responses;
    /// once accepted, the answer streams as Server-Sent Events (status, delta, citations, done | error).
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Chat)]
    [RequestSizeLimit(ChatRequestReader.MaxRequestBodyBytes)]
    public async Task Ask(CancellationToken cancellationToken)
    {
        var session = HttpContext.GetCurrentSession();
        var question = await ChatRequestReader.ReadQuestionAsync(Request, cancellationToken);
        using var questionLease = chatService.BeginQuestion(session);

        var events = new ServerSentEventWriter(Response, jsonOptions.Value.SerializerOptions);
        await events.StartAsync(cancellationToken);
        await chatService.StreamAnswerAsync(session, question, events, ApiErrors.GetTraceId(HttpContext), cancellationToken);
    }

    /// <summary>Returns the session's conversation, oldest first.</summary>
    [HttpGet("history")]
    [ProducesResponseType<ChatHistoryResponse>(StatusCodes.Status200OK)]
    public IActionResult History() =>
        Ok(new ChatHistoryResponse(HttpContext.GetCurrentSession().Conversation.GetMessages()));
}
