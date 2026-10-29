using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace DocumentReader.Evals;

/// <summary>
/// A fact that only runs when a Claude API key is available, so the evals are skipped (not passed)
/// on machines and pipelines without one.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!LiveConfiguration.HasApiKey)
        {
            Skip = "Live eval: set Anthropic:ApiKey (User Secrets of DocumentReader.Api) or Anthropic__ApiKey to run.";
        }
    }
}

public static class LiveConfiguration
{
    public static bool HasApiKey { get; } = !string.IsNullOrWhiteSpace(
        new ConfigurationBuilder()
            .AddUserSecrets(typeof(Program).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build()["Anthropic:ApiKey"]);
}

/// <summary>The real API with the real Claude client; Development environment so User Secrets are loaded.</summary>
public sealed class LiveApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Limits:ChatRequestsPerMinute"] = "1000",
                ["Limits:UploadRequestsPerMinute"] = "1000",
                ["Limits:SessionCreationRequestsPerMinute"] = "1000",
            }));
    }
}

/// <summary>The final answer of one chat request, parsed from the SSE stream.</summary>
public sealed record EvalAnswer(string Answer, bool Incomplete, IReadOnlyList<string> CitedEvidenceIds, string RawEvents);

/// <summary>Drives the HTTP API the way the browser client does.</summary>
public sealed class EvalClient(HttpClient client)
{
    private string? _sessionId;

    public async Task StartSessionAsync()
    {
        var response = await client.PostAsync("/api/sessions", content: null);
        response.EnsureSuccessStatusCode();
        _sessionId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString();
    }

    public async Task UploadAsync(params (string FileName, byte[] Content)[] files)
    {
        using var multipart = new MultipartFormDataContent();
        foreach (var (fileName, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(part, "files", fileName);
        }

        var response = await SendAsync(HttpMethod.Post, "/api/documents", multipart);
        response.EnsureSuccessStatusCode();
    }

    public async Task<EvalAnswer> AskAsync(string question)
    {
        var body = new StringContent(JsonSerializer.Serialize(new { question }), Encoding.UTF8, "application/json");
        var response = await SendAsync(HttpMethod.Post, "/api/chat", body);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        JsonElement? done = null;
        var cited = new List<string>();
        foreach (var block in raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.First(line => line.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = JsonDocument.Parse(lines.First(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]).RootElement;
            if (name == "citations")
            {
                cited.AddRange(data.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("evidenceId").GetString()!));
            }
            else if (name == "done")
            {
                done = data;
            }
            else if (name == "error")
            {
                Assert.Fail($"The chat stream returned an error: {data.GetRawText()}");
            }
        }

        Assert.True(done.HasValue, "The chat stream ended without a done event.");
        return new EvalAnswer(done.Value.GetProperty("answer").GetString()!, done.Value.GetProperty("incomplete").GetBoolean(), cited, raw);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(SessionHeader.Name, _sessionId ?? throw new InvalidOperationException("Start a session first."));
        return await client.SendAsync(request);
    }
}
