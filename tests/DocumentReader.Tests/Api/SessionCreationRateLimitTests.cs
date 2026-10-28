using System.Net;
using DocumentReader.Api.Errors;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

public sealed class SessionCreationRateLimitTests(SessionCreationRateLimitTests.LowSessionLimitFactory factory)
    : IClassFixture<SessionCreationRateLimitTests.LowSessionLimitFactory>
{
    private const int SessionCreationLimit = 2;

    [Fact]
    public async Task CreateSession_OverPerMinuteLimit_Returns429RateLimited()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < SessionCreationLimit; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/sessions", content: null)).StatusCode);
        }

        var response = await client.PostAsync("/api/sessions", content: null);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    public sealed class LowSessionLimitFactory : ApiFactory
    {
        protected override Dictionary<string, string?> GetSettings()
        {
            var settings = base.GetSettings();
            settings["Limits:SessionCreationRequestsPerMinute"] = SessionCreationLimit.ToString();
            return settings;
        }
    }
}
