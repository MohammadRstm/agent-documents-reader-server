using System.Net;
using System.Text;
using DocumentReader.Api.Errors;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

public sealed class ErrorResponseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task UnhandledException_Returns500InternalErrorWithoutExceptionDetails()
    {
        var response = await _client.GetAsync("/api/test/throw");

        var root = await ErrorAssertions.AssertErrorAsync(
            response, HttpStatusCode.InternalServerError, ErrorCodes.InternalError);
        var rawBody = root.GetRawText();
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", rawBody);
        Assert.DoesNotContain("   at ", rawBody);
        Assert.False(root.TryGetProperty("details", out _));
    }

    [Fact]
    public async Task InvalidModel_Returns400ValidationFailedWithCamelCaseFieldDetails()
    {
        var response = await PostJsonAsync("/api/test/validate", """{ "question": "much longer than ten characters" }""");

        var root = await ErrorAssertions.AssertErrorAsync(
            response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        var questionErrors = root.GetProperty("details").GetProperty("question");
        Assert.Equal("Question is too long.", questionErrors[0].GetString());
    }

    [Fact]
    public async Task MissingRequiredField_Returns400ValidationFailed()
    {
        var response = await PostJsonAsync("/api/test/validate", "{}");

        var root = await ErrorAssertions.AssertErrorAsync(
            response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.True(root.GetProperty("details").TryGetProperty("question", out _));
    }

    [Fact]
    public async Task MalformedJson_Returns400ValidationFailedWithoutSerializerInternals()
    {
        var response = await PostJsonAsync("/api/test/validate", """{ "question": 12""");

        var root = await ErrorAssertions.AssertErrorAsync(
            response, HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
        Assert.DoesNotContain("System.", root.GetRawText());
    }

    [Fact]
    public async Task RateLimitExceeded_Returns429RateLimited()
    {
        for (var i = 0; i < ApiFactory.ChatRequestsPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/test/limited")).StatusCode);
        }

        var response = await _client.GetAsync("/api/test/limited");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, string json) =>
        _client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
}
