namespace DocumentReader.Api.Sessions;

public static class HttpContextSessionExtensions
{
    private static readonly object SessionItemKey = new();

    internal static void SetCurrentSession(this HttpContext httpContext, Session session) =>
        httpContext.Items[SessionItemKey] = session;

    /// <summary>
    /// Returns the session resolved by <see cref="RequireSessionFilter"/>. Throws when called from an
    /// action that opted out of the session requirement, which is a programming error.
    /// </summary>
    public static Session GetCurrentSession(this HttpContext httpContext) =>
        httpContext.Items[SessionItemKey] as Session
        ?? throw new InvalidOperationException("No session was resolved for this request.");
}
