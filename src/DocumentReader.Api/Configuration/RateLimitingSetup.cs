using System.Globalization;
using System.Threading.RateLimiting;
using DocumentReader.Api.Errors;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocumentReader.Api.Configuration;

/// <summary>
/// Names of the rate-limiting policies. Apply them with <c>[EnableRateLimiting(RateLimitPolicies.Upload)]</c>.
/// </summary>
public static class RateLimitPolicies
{
    public const string SessionCreation = "session-creation";
    public const string Upload = "upload";
    public const string Chat = "chat";
}

public static class RateLimitingSetup
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Registers fixed-window limits partitioned by client IP address. Partitioning by IP rather
    /// than by session ID means creating extra sessions does not bypass the limit.
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.AddPolicy(RateLimitPolicies.SessionCreation, httpContext =>
                CreatePerClientLimit(httpContext, limits => limits.SessionCreationRequestsPerMinute));
            options.AddPolicy(RateLimitPolicies.Upload, httpContext =>
                CreatePerClientLimit(httpContext, limits => limits.UploadRequestsPerMinute));
            options.AddPolicy(RateLimitPolicies.Chat, httpContext =>
                CreatePerClientLimit(httpContext, limits => limits.ChatRequestsPerMinute));
        });

        return services;
    }

    private static RateLimitPartition<string> CreatePerClientLimit(
        HttpContext httpContext,
        Func<LimitsOptions, int> selectPermitLimit)
    {
        var limits = httpContext.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = selectPermitLimit(limits),
            Window = Window,
            QueueLimit = 0,
        });
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await ApiErrors.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            "Too many requests. Please wait and try again.",
            cancellationToken);
    }
}
