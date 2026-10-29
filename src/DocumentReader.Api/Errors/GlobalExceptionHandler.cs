using Microsoft.AspNetCore.Diagnostics;

namespace DocumentReader.Api.Errors;

/// <summary>
/// Converts exceptions that escape the request pipeline into contract-shaped error responses.
/// Exception details are logged server-side only; clients receive a generic, safe message.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = ApiErrors.GetTraceId(httpContext);

        // The client disconnected (surfacing as cancellation or an I/O error); nobody is left to answer.
        if (httpContext.RequestAborted.IsCancellationRequested
            && exception is OperationCanceledException or IOException)
        {
            logger.LogInformation("Request was aborted by the client. TraceId: {TraceId}", traceId);
            return true;
        }

        int statusCode;
        string code;
        string message;
        IReadOnlyDictionary<string, string[]>? details = null;

        if (exception is ApiException apiException)
        {
            // Expected client error; the message was written to be safe for clients.
            logger.LogInformation(
                "Request rejected with {StatusCode} {ErrorCode}. TraceId: {TraceId}",
                apiException.StatusCode,
                apiException.Code,
                traceId);
            statusCode = apiException.StatusCode;
            code = apiException.Code;
            message = apiException.Message;
            details = apiException.Details;
        }
        else if (exception is BadHttpRequestException badRequest)
        {
            // Raised by the server for malformed requests, including body-size limit violations.
            logger.LogWarning("Bad HTTP request ({StatusCode}). TraceId: {TraceId}", badRequest.StatusCode, traceId);
            if (badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                statusCode = StatusCodes.Status413PayloadTooLarge;
                code = ErrorCodes.PayloadTooLarge;
                message = "The request body is too large.";
            }
            else
            {
                statusCode = StatusCodes.Status400BadRequest;
                code = ErrorCodes.ValidationFailed;
                message = "The request could not be read.";
            }
        }
        else
        {
            logger.LogError(exception, "Unhandled exception. TraceId: {TraceId}", traceId);
            statusCode = StatusCodes.Status500InternalServerError;
            code = ErrorCodes.InternalError;
            message = "An unexpected error occurred.";
        }

        await ApiErrors.WriteAsync(httpContext, statusCode, code, message, cancellationToken, details);
        return true;
    }
}
