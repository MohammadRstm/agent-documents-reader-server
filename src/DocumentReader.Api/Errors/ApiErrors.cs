using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace DocumentReader.Api.Errors;

/// <summary>
/// Builds contract-shaped error responses so every error path (filters, middleware,
/// rate limiter, exception handler) produces the same body and trace ID.
/// </summary>
public static class ApiErrors
{
    public static string GetTraceId(HttpContext httpContext) =>
        Activity.Current?.Id ?? httpContext.TraceIdentifier;

    public static ErrorResponse Create(
        HttpContext httpContext,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, GetTraceId(httpContext), details);

    /// <summary>Creates an MVC result for use in controllers and filters.</summary>
    public static ObjectResult ToResult(
        HttpContext httpContext,
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null)
    {
        var result = new ObjectResult(Create(httpContext, code, message, details))
        {
            StatusCode = statusCode,
        };
        result.ContentTypes.Add("application/json");
        return result;
    }

    /// <summary>Writes an error directly to the response, for code running outside MVC.</summary>
    public static Task WriteAsync(
        HttpContext httpContext,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string[]>? details = null)
    {
        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(
            Create(httpContext, code, message, details),
            cancellationToken);
    }
}
