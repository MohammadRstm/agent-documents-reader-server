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

        // The client disconnected; there is nobody left to send a response to.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request was cancelled by the client. TraceId: {TraceId}", traceId);
            return true;
        }

        int statusCode;
        string code;
        string message;

        if (exception is BadHttpRequestException badRequest)
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

        await ApiErrors.WriteAsync(httpContext, statusCode, code, message, cancellationToken);
        return true;
    }
}
