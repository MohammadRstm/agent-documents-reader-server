using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;

namespace DocumentReader.Tests.Api;

public sealed class EvidenceEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_PdfEvidence_ReturnsDetailWithPageSource()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        await UploadAsync(sessionId, "contract.pdf", PdfFixtures.WithPageTexts("Payment terms are 30 days.", "Second page"));

        var response = await GetAsync(sessionId, "E2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var evidence = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("E2", evidence.GetProperty("evidenceId").GetString());
        Assert.Equal("contract.pdf", evidence.GetProperty("fileName").GetString());
        Assert.Equal("textChunk", evidence.GetProperty("kind").GetString());
        Assert.Equal("pdfPage", evidence.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(2, evidence.GetProperty("source").GetProperty("page").GetInt32());
        Assert.Contains("Second page", evidence.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Get_ExcelRowEvidence_ReturnsSheetAndRange()
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        await UploadAsync(sessionId, "boq.xlsx", ExcelFixtures.BillOfQuantities());

        // Evidence IDs follow table rows in order: row 4 is E1, row 5 is E2.
        var response = await GetAsync(sessionId, "E2");

        var evidence = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("tableRow", evidence.GetProperty("kind").GetString());
        var source = evidence.GetProperty("source");
        Assert.Equal("excelRange", source.GetProperty("type").GetString());
        Assert.Equal("BOQ", source.GetProperty("sheet").GetString());
        Assert.Equal("B5:G5", source.GetProperty("range").GetString());
    }

    [Theory]
    [InlineData("E999")]
    [InlineData("e1")]
    [InlineData("not-an-id")]
    public async Task Get_UnknownOrMalformedId_Returns404EvidenceNotFound(string evidenceId)
    {
        var sessionId = await factory.CreateSessionAsync(_client);
        await UploadAsync(sessionId, "contract.pdf", PdfFixtures.WithPageTexts("Text"));

        var response = await GetAsync(sessionId, evidenceId);

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.EvidenceNotFound);
    }

    [Fact]
    public async Task Get_EvidenceOfAnotherSession_Returns404()
    {
        var owner = await factory.CreateSessionAsync(_client);
        var other = await factory.CreateSessionAsync(_client);
        await UploadAsync(owner, "private.pdf", PdfFixtures.WithPageTexts("Confidential"));

        var response = await GetAsync(other, "E1");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.EvidenceNotFound);
    }

    [Fact]
    public async Task Get_WithoutSession_Returns400SessionRequired()
    {
        var response = await _client.GetAsync("/api/evidence/E1");

        await ErrorAssertions.AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.SessionRequired);
    }

    private async Task<HttpResponseMessage> GetAsync(string sessionId, string evidenceId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/evidence/{Uri.EscapeDataString(evidenceId)}");
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request);
    }

    private async Task UploadAsync(string sessionId, string fileName, byte[] content)
    {
        using var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(part, "files", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = multipart };
        request.Headers.Add(SessionHeader.Name, sessionId);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }
}
