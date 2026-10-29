using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Llm;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentReader.Tests.Api;

public sealed class ChatEndpointTests : IClassFixture<ChatEndpointTests.ChatApiFactory>, IDisposable
{
    private readonly ChatApiFactory _factory;
    private readonly HttpClient _client;

    public ChatEndpointTests(ChatApiFactory factory)
    {
        _factory = factory;
        _factory.Model.Reset();
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Ask_StreamsStatusDeltaCitationsAndDone()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.ThenCallTools(("search_text", """{ "query": "payment terms" }""")).ThenAnswer("Payment is due in 30 days [E1].");

        var response = await AskAsync(sessionId, "When is payment due?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        Assert.Equal("status", events[0].Name);
        Assert.Contains(events, e => e.Name == "status" && e.Data.GetProperty("tool").GetString() == "search_text");
        Assert.Equal("Payment is due in 30 days [E1].", string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString())));
        Assert.Equal(["citations", "done"], events.TakeLast(2).Select(e => e.Name));

        var citation = Assert.Single(events[^2].Data.GetProperty("citations").EnumerateArray());
        Assert.Equal("E1", citation.GetProperty("evidenceId").GetString());
        Assert.Equal("contract.pdf", citation.GetProperty("fileName").GetString());
        Assert.Equal("pdfPage", citation.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(1, citation.GetProperty("source").GetProperty("page").GetInt32());

        var done = events[^1].Data;
        Assert.Equal("Payment is due in 30 days [E1].", done.GetProperty("answer").GetString());
        Assert.False(done.GetProperty("incomplete").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(done.GetProperty("messageId").GetString()));
    }

    [Fact]
    public async Task Ask_UnverifiedCitations_AreRemovedFromTheFinalAnswer()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.ThenAnswer("Due in 30 days [E1] and invented [E99].");

        var events = ParseEvents(await (await AskAsync(sessionId, "When is payment due?")).Content.ReadAsStringAsync());

        Assert.Empty(events[^2].Data.GetProperty("citations").EnumerateArray());
        Assert.Equal("Due in 30 days and invented.", events[^1].Data.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Ask_ProviderFailure_SendsErrorEventAndKeepsHistoryUnchanged()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.ThenThrow(new ChatModelUnavailableException("provider down: secret detail"));

        var response = await AskAsync(sessionId, "Question");

        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        var error = events[^1];
        Assert.Equal("error", error.Name);
        Assert.Equal(ErrorCodes.ProviderUnavailable, error.Data.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.Data.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("secret", error.Data.GetRawText());
        Assert.DoesNotContain(events, e => e.Name == "done");
        Assert.Empty(await HistoryAsync(sessionId));
    }

    [Fact]
    public async Task History_ReturnsQuestionAndAnswerWithCitations()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.ThenCallTools(("get_evidence", """{ "evidenceIds": ["E1"] }""")).ThenAnswer("Thirty days [E1].");
        await (await AskAsync(sessionId, "  When is payment due?  ")).Content.ReadAsStringAsync();

        var messages = await HistoryAsync(sessionId);

        Assert.Equal(["user", "assistant"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("When is payment due?", messages[0].GetProperty("content").GetString());
        Assert.Empty(messages[0].GetProperty("citations").EnumerateArray());
        Assert.Equal("Thirty days [E1].", messages[1].GetProperty("content").GetString());
        Assert.Equal("E1", messages[1].GetProperty("citations")[0].GetProperty("evidenceId").GetString());
        Assert.False(messages[1].GetProperty("incomplete").GetBoolean());
        Assert.EndsWith("Z", messages[1].GetProperty("createdAt").GetString());
    }

    [Fact]
    public async Task Ask_FollowUpQuestion_SendsEarlierExchangeToTheModel()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.ThenAnswer("First answer.").ThenAnswer("Second answer.");
        await (await AskAsync(sessionId, "First question")).Content.ReadAsStringAsync();

        await (await AskAsync(sessionId, "Second question")).Content.ReadAsStringAsync();

        var messages = _factory.Model.Requests[^1].Messages;
        Assert.Equal("First question", Assert.IsType<UserTextTurn>(messages[0]).Text);
        Assert.Equal("First answer.", Assert.IsType<AssistantTextTurn>(messages[1]).Text);
        Assert.Equal("Second question", Assert.IsType<UserTextTurn>(messages[2]).Text);
    }

    [Fact]
    public async Task Ask_WhileAnotherQuestionIsStreaming_Returns409()
    {
        var sessionId = await CreateSessionWithContractAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Model.ThenWaitFor(started, release.Task, "Finished.");

        var first = AskAsync(sessionId, "Slow question");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await AskAsync(sessionId, "Impatient question");
        release.SetResult();
        var firstEvents = ParseEvents(await (await first).Content.ReadAsStringAsync());

        await ErrorAssertions.AssertErrorAsync(second, HttpStatusCode.Conflict, ErrorCodes.QuestionInProgress);
        Assert.Equal("done", firstEvents[^1].Name);
    }

    [Fact]
    public async Task Ask_WithoutApiKey_Returns502BeforeStreaming()
    {
        var sessionId = await CreateSessionWithContractAsync();
        _factory.Model.IsConfigured = false;

        var response = await AskAsync(sessionId, "Question");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadGateway, ErrorCodes.ProviderUnavailable);
    }

    [Fact]
    public async Task Ask_NonJsonBody_Returns415()
    {
        var sessionId = await CreateSessionWithContractAsync();

        var response = await SendAsync(sessionId, new StringContent("question=hi", Encoding.UTF8, "application/x-www-form-urlencoded"));

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, ErrorCodes.UnsupportedMediaType);
    }

    [Theory]
    [InlineData("""{ "question": "   " }""")]
    [InlineData("""{ "question": 5 }""")]
    [InlineData("""{ }""")]
    [InlineData("""{ "question": "x" """)]
    [InlineData("""[]""")]
    public async Task Ask_InvalidBody_Returns400ValidationFailed(string json)
    {
        var sessionId = await CreateSessionWithContractAsync();

        var response = await SendAsync(sessionId, new StringContent(json, Encoding.UTF8, "application/json"));

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.Empty(_factory.Model.Requests);
    }

    [Fact]
    public async Task Ask_QuestionTooLong_Returns400WithQuestionDetail()
    {
        var sessionId = await CreateSessionWithContractAsync();

        var response = await AskAsync(sessionId, new string('q', 4001));

        var root = await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(root.GetProperty("details").TryGetProperty("question", out _));
    }

    [Fact]
    public async Task Ask_WithoutSession_Returns400SessionRequired()
    {
        var response = await _client.PostAsync("/api/chat", new StringContent("""{ "question": "Hi" }""", Encoding.UTF8, "application/json"));

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    private async Task<string> CreateSessionWithContractAsync()
    {
        var sessionId = await _factory.CreateSessionAsync(_client);
        var store = _factory.Services.GetRequiredService<ISessionStore>();
        Assert.True(store.TryGetSession(sessionId, out var session));
        Assert.True(session.Documents.TryAdd(
            [TestSessions.TextDocument("contract.pdf", "Payment terms: 30 days from invoice.")],
            maxContentDocuments: 20));
        return sessionId;
    }

    private Task<HttpResponseMessage> AskAsync(string sessionId, string question) =>
        SendAsync(sessionId, new StringContent(JsonSerializer.Serialize(new { question }), Encoding.UTF8, "application/json"));

    private async Task<HttpResponseMessage> SendAsync(string sessionId, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = content };
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request);
    }

    private async Task<List<JsonElement>> HistoryAsync(string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/chat/history");
        request.Headers.Add(SessionHeader.Name, sessionId);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("messages").EnumerateArray().ToList();
    }

    /// <summary>Parses "event: name / data: json" blocks separated by blank lines.</summary>
    private static List<(string Name, JsonElement Data)> ParseEvents(string body)
    {
        var events = new List<(string, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(line => line.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
            var data = lines.Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            events.Add((name, TestSessions.Json(data)));
        }

        return events;
    }

    /// <summary>The API with the scripted model instead of the real Claude client.</summary>
    public sealed class ChatApiFactory : ApiFactory
    {
        public ScriptedChatModelClient Model { get; } = new();

        protected override Dictionary<string, string?> GetSettings()
        {
            var settings = base.GetSettings();
            settings["Limits:ChatRequestsPerMinute"] = "10000";
            return settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IChatModelClient>(Model));
        }
    }
}
