using System.Net;
using System.Text.Json;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

public sealed class SessionEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task CreateSession_Returns201WithSessionIdAndUtcTimestamp()
    {
        var response = await _client.PostAsync("/api/sessions", content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var sessionId = root.GetProperty("sessionId").GetString();
        Assert.True(SessionIdGenerator.IsWellFormed(sessionId));

        var createdAt = root.GetProperty("createdAt").GetString();
        Assert.NotNull(createdAt);
        Assert.EndsWith("Z", createdAt);
        Assert.Equal(factory.Clock.GetUtcNow(), DateTimeOffset.Parse(createdAt));
    }

    [Fact]
    public async Task CreateSession_ReturnsDifferentIdForEachSession()
    {
        var first = await factory.CreateSessionAsync(_client);
        var second = await factory.CreateSessionAsync(_client);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Request_WithValidSession_ResolvesThatSession()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        var response = await SendWithSessionAsync(sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(sessionId, root.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Request_WithoutSessionHeader_Returns400SessionRequired()
    {
        var response = await _client.GetAsync("/api/test/session");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    [Theory]
    [InlineData("not-a-session")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Request_WithMalformedSessionHeader_Returns400SessionRequired(string sessionId)
    {
        var response = await SendWithSessionAsync(sessionId);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    [Fact]
    public async Task Request_WithTwoSessionHeaders_Returns400SessionRequired()
    {
        var first = await factory.CreateSessionAsync(_client);
        var second = await factory.CreateSessionAsync(_client);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        request.Headers.Add(SessionHeader.Name, [first, second]);

        var response = await _client.SendAsync(request);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    [Fact]
    public async Task Request_WithUnknownSessionId_Returns404SessionNotFound()
    {
        var response = await SendWithSessionAsync(SessionIdGenerator.Create());

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.SessionNotFound);
    }

    [Fact]
    public async Task Request_AfterIdleTimeout_Returns404SessionNotFound()
    {
        var sessionId = await factory.CreateSessionAsync(_client);

        factory.Clock.Advance(TimeSpan.FromMinutes(ApiFactory.IdleTimeoutMinutes));
        var response = await SendWithSessionAsync(sessionId);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.SessionNotFound);
    }

    [Fact]
    public async Task Request_BeforeIdleTimeout_KeepsSessionAlive()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        var almostIdleTimeout = TimeSpan.FromMinutes(ApiFactory.IdleTimeoutMinutes - 1);

        factory.Clock.Advance(almostIdleTimeout);
        Assert.Equal(HttpStatusCode.OK, (await SendWithSessionAsync(sessionId)).StatusCode);

        // The previous request reset the idle timer, so the session is still alive.
        factory.Clock.Advance(almostIdleTimeout);
        Assert.Equal(HttpStatusCode.OK, (await SendWithSessionAsync(sessionId)).StatusCode);
    }

    private async Task<HttpResponseMessage> SendWithSessionAsync(string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/test/session");
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request);
    }
}
