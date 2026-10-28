using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocumentReader.Api.Controllers;

/// <summary>
/// Stops MVC model binding from reading the request form before the action runs. Without this,
/// MVC reads multipart bodies with the server's default limits and turns failures (such as an
/// oversized body) into generic validation errors. The upload action reads the form itself so it
/// can apply the configured limits and return the contract's 413 response.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
