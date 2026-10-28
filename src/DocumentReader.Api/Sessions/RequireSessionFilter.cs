using DocumentReader.Api.Errors;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DocumentReader.Api.Sessions;

/// <summary>
/// Global filter that resolves the X-Session-Id header to a live session before model binding,
/// so session errors are returned before the request body is read or validated.
/// Actions marked with <see cref="AllowWithoutSessionAttribute"/> are skipped.
/// </summary>
public sealed class RequireSessionFilter(ISessionStore sessionStore) : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AllowWithoutSessionAttribute>().Any())
        {
            return;
        }

        var httpContext = context.HttpContext;
        var headerValues = httpContext.Request.Headers[SessionHeader.Name];
        var sessionId = headerValues.Count == 1 ? headerValues[0] : null;

        if (sessionId is null || !SessionIdGenerator.IsWellFormed(sessionId))
        {
            context.Result = ApiErrors.ToResult(
                httpContext,
                StatusCodes.Status400BadRequest,
                ErrorCodes.SessionRequired,
                $"The {SessionHeader.Name} header is missing or malformed. Create a session first.");
            return;
        }

        if (!sessionStore.TryGetSession(sessionId, out var session))
        {
            context.Result = ApiErrors.ToResult(
                httpContext,
                StatusCodes.Status404NotFound,
                ErrorCodes.SessionNotFound,
                "The session does not exist or has expired. Create a new session.");
            return;
        }

        httpContext.SetCurrentSession(session);
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
