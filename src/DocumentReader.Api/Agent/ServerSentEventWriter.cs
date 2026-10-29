using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace DocumentReader.Api.Agent;

/// <summary>
/// Writes the chat response as Server-Sent Events: "event: name", "data: single-line JSON", blank
/// line. Each event is flushed immediately so the client sees progress as it happens.
/// </summary>
public sealed class ServerSentEventWriter(HttpResponse response, JsonSerializerOptions jsonOptions) : IAgentEventSink
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        response.HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.StartAsync(cancellationToken);
    }

    public Task StatusAsync(string message, string? tool, CancellationToken cancellationToken) =>
        WriteAsync("status", new { message, tool }, cancellationToken);

    public Task DeltaAsync(string text, CancellationToken cancellationToken) =>
        WriteAsync("delta", new { text }, cancellationToken);

    public async Task WriteAsync(string eventName, object payload, CancellationToken cancellationToken)
    {
        // System.Text.Json writes compact JSON, which never contains a raw line break.
        var data = JsonSerializer.Serialize(payload, jsonOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
