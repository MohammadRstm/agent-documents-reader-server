using System.Net.Http.Json;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>
/// Hosts the real API in memory with a controllable clock, a low chat rate limit and the
/// test-only endpoints in <see cref="TestEndpointsController"/>. Subclasses can override
/// <see cref="GetSettings"/> to change configuration for a single test class.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const int ChatRequestsPerMinute = 2;
    public const int IdleTimeoutMinutes = 60;

    public ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Configuration values applied on top of appsettings.json.</summary>
    protected virtual Dictionary<string, string?> GetSettings() => new()
    {
        ["Limits:ChatRequestsPerMinute"] = ChatRequestsPerMinute.ToString(),
        // All test requests share one partition, so keep session creation effectively unlimited.
        ["Limits:SessionCreationRequestsPerMinute"] = "10000",
        ["Limits:UploadRequestsPerMinute"] = "10000",
        ["Sessions:IdleTimeoutMinutes"] = IdleTimeoutMinutes.ToString(),
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development", so local User Secrets (such as a real API key) are never loaded by tests.
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(GetSettings()));

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddControllers().AddApplicationPart(typeof(TestEndpointsController).Assembly);
        });
    }

    public async Task<string> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/sessions", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateSessionResponse>();
        return body!.SessionId;
    }
}
