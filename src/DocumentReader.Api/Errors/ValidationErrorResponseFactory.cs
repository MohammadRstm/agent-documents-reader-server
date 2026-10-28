using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocumentReader.Api.Errors;

/// <summary>
/// Replaces the default ProblemDetails response for invalid model state with the
/// contract's validation_failed error, including per-field details.
/// </summary>
public static class ValidationErrorResponseFactory
{
    public static IActionResult Create(ActionContext context)
    {
        var details = new Dictionary<string, string[]>();
        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.ValidationState != ModelValidationState.Invalid || entry.Errors.Count == 0)
            {
                continue;
            }

            var messages = entry.Errors
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The value is invalid." : error.ErrorMessage)
                .ToArray();
            details[ToFieldName(key)] = messages;
        }

        return ApiErrors.ToResult(
            context.HttpContext,
            StatusCodes.Status400BadRequest,
            ErrorCodes.ValidationFailed,
            "The request is invalid.",
            details);
    }

    /// <summary>Maps model-state keys such as "Question" or "$.question" to camelCase field names.</summary>
    private static string ToFieldName(string modelStateKey)
    {
        var key = modelStateKey.StartsWith("$.", StringComparison.Ordinal) ? modelStateKey[2..] : modelStateKey;
        if (key.Length == 0 || key == "$")
        {
            return "body";
        }

        return JsonNamingPolicy.CamelCase.ConvertName(key);
    }
}
