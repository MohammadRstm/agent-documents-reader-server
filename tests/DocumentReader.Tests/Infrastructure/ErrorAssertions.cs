using System.Net;
using System.Text.Json;

namespace DocumentReader.Tests.Infrastructure;

public static class ErrorAssertions
{
    /// <summary>
    /// Asserts that the response uses the contract error shape with the expected status and code,
    /// and returns the parsed body for further checks.
    /// </summary>
    public static async Task<JsonElement> AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        return root;
    }
}
