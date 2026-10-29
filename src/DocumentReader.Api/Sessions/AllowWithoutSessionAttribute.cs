namespace DocumentReader.Api.Sessions;

/// <summary>
/// Opts an action out of the global session requirement. Only <c>POST /api/sessions</c> should use it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class AllowWithoutSessionAttribute : Attribute;
