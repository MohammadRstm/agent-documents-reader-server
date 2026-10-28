using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Llm;
using DocumentReader.Api.Sessions;
using DocumentReader.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DocumentReader.Tests.Api;

/// <summary>
/// Parallel parsing, the server-wide extraction limit, per-file timeouts, parallel uploads to one
/// session, and the chat checks that depend on uploads (documents_processing, no_documents).
/// </summary>
public sealed class UploadConcurrencyTests : IClassFixture<UploadConcurrencyTests.ConcurrencyFactory>
{
    private const string HangMarker = "%HANG";
    private readonly ConcurrencyFactory _factory;
    private readonly HttpClient _client;

    public UploadConcurrencyTests(ConcurrencyFactory factory)
    {
        _factory = factory;
        _factory.Extractor.Reset();
        _factory.Model.Reset();
        _client = factory.CreateClient();
    }

    private ControllableExtractor Extractor => _factory.Extractor;

    [Fact]
    public async Task OneRequest_FilesAreParsedConcurrentlyWithinTheServerWideLimit()
    {
        var limiter = _factory.Services.GetRequiredService<ExtractionLimiter>();
        var expectedOverlap = limiter.MaxConcurrency;
        Extractor.Behavior = (_, _) =>
        {
            Extractor.WaitForConcurrent(expectedOverlap);
            Thread.Sleep(50);
            return ControllableExtractor.Indexed();
        };
        var sessionId = await _factory.CreateSessionAsync(_client);

        var response = await UploadAsync(sessionId, Pdfs("a.pdf", "b.pdf", "c.pdf", "d.pdf"));

        var documents = await ReadDocumentsAsync(response);
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf", "d.pdf"], documents.Select(document => document.GetProperty("fileName").GetString()));
        Assert.All(documents, document => Assert.Equal("indexed", document.GetProperty("status").GetString()));
        Assert.Equal(expectedOverlap, Extractor.MaxActive);
        Assert.True(limiter.MaxConcurrency <= ConcurrencyFactory.MaxParallelExtractions);
        if (Environment.ProcessorCount >= 2)
        {
            Assert.True(Extractor.MaxActive >= 2, "Files of one request should be parsed in parallel.");
        }
    }

    [Fact]
    public async Task HangingFile_TimesOutWhileOtherFilesComplete()
    {
        using var hangRelease = new ManualResetEventSlim();
        Extractor.Behavior = (content, _) =>
        {
            if (ControllableExtractor.HasMarker(content, HangMarker))
            {
                // Ignores cancellation, like a library stuck in a call; released when the test ends.
                hangRelease.Wait(TimeSpan.FromSeconds(10));
            }

            return ControllableExtractor.Indexed();
        };
        var sessionId = await _factory.CreateSessionAsync(_client);
        var hanging = PdfFixtures.WithPageTexts("Hangs").Concat(Encoding.ASCII.GetBytes(HangMarker)).ToArray();

        try
        {
            var response = await UploadAsync(
                sessionId,
                ("first.pdf", PdfFixtures.WithPageTexts("First")),
                ("stuck.pdf", hanging),
                ("last.pdf", PdfFixtures.WithPageTexts("Last")));

            var documents = await ReadDocumentsAsync(response);
            Assert.Equal(["indexed", "corrupt", "indexed"], documents.Select(document => document.GetProperty("status").GetString()));
            Assert.Equal(UploadMessages.TimedOut, documents[1].GetProperty("warnings")[0].GetString());
        }
        finally
        {
            hangRelease.Set();
        }
    }

    [Fact]
    public async Task ParallelUploadsToOneSession_BothSucceed()
    {
        Extractor.Behavior = (_, _) =>
        {
            Extractor.WaitForConcurrent(2);
            return ControllableExtractor.Indexed();
        };
        var sessionId = await _factory.CreateSessionAsync(_client);

        var responses = await Task.WhenAll(
            UploadAsync(sessionId, Pdfs("a1.pdf", "a2.pdf")),
            UploadAsync(sessionId, Pdfs("b1.pdf", "b2.pdf")));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(4, (await ListDocumentsAsync(sessionId)).Count);
        Assert.Equal(0, Session(sessionId).Uploads.InProgress);
    }

    [Fact]
    public async Task ParallelUploadsExceedingTheSessionLimit_OnlyOneIsAccepted()
    {
        var sessionId = await _factory.CreateSessionAsync(_client);

        var responses = await Task.WhenAll(
            UploadAsync(sessionId, Pdfs("a1.pdf", "a2.pdf", "a3.pdf")),
            UploadAsync(sessionId, Pdfs("b1.pdf", "b2.pdf", "b3.pdf")));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var rejected = Assert.Single(responses, response => response.StatusCode != HttpStatusCode.OK);
        await ErrorAssertions.AssertErrorAsync(rejected, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.PayloadTooLarge);
        Assert.Equal(3, Session(sessionId).Documents.ContentDocumentCount);
    }

    [Fact]
    public async Task Chat_WhileAnUploadIsProcessing_Returns409DocumentsProcessing()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Extractor.Behavior = (_, cancellationToken) =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            return ControllableExtractor.Indexed();
        };
        var sessionId = await _factory.CreateSessionAsync(_client);

        var upload = UploadAsync(sessionId, Pdfs("slow.pdf"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        var duringUpload = await AskAsync(sessionId);
        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await upload).StatusCode);

        await ErrorAssertions.AssertErrorAsync(duringUpload, HttpStatusCode.Conflict, ErrorCodes.DocumentsProcessing);
        Assert.Equal(0, Session(sessionId).Uploads.InProgress);

        _factory.Model.ThenAnswer("Answer.");
        var afterUpload = await AskAsync(sessionId);
        Assert.Equal(HttpStatusCode.OK, afterUpload.StatusCode);
    }

    [Fact]
    public async Task Chat_WithoutReadableDocuments_Returns409NoDocuments()
    {
        var sessionId = await _factory.CreateSessionAsync(_client);

        await ErrorAssertions.AssertErrorAsync(await AskAsync(sessionId), HttpStatusCode.Conflict, ErrorCodes.NoDocuments);

        var upload = await UploadAsync(sessionId, ("notes.pdf", Encoding.UTF8.GetBytes("Not really a PDF")));
        Assert.Equal("unsupported", Assert.Single(await ReadDocumentsAsync(upload)).GetProperty("status").GetString());
        await ErrorAssertions.AssertErrorAsync(await AskAsync(sessionId), HttpStatusCode.Conflict, ErrorCodes.NoDocuments);
        Assert.Empty(_factory.Model.Requests);
    }

    [Fact]
    public async Task CancelledUpload_ProcessingCounterReturnsToZero()
    {
        using var started = new ManualResetEventSlim();
        Extractor.Behavior = (_, cancellationToken) =>
        {
            started.Set();
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return ControllableExtractor.Indexed();
        };
        var sessionId = await _factory.CreateSessionAsync(_client);
        using var cancellation = new CancellationTokenSource();

        var upload = UploadAsync(sessionId, Pdfs("cancelled.pdf"), cancellation.Token);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Session(sessionId).Uploads.InProgress);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        Assert.True(
            SpinWait.SpinUntil(() => Session(sessionId).Uploads.InProgress == 0, TimeSpan.FromSeconds(15)),
            "The upload counter should return to zero after the request ends.");
    }

    private static (string FileName, byte[] Content)[] Pdfs(params string[] names) =>
        names.Select(name => (name, PdfFixtures.WithPageTexts(name))).ToArray();

    private Session Session(string sessionId)
    {
        Assert.True(_factory.Services.GetRequiredService<ISessionStore>().TryGetSession(sessionId, out var session));
        return session;
    }

    private async Task<HttpResponseMessage> UploadAsync(
        string sessionId,
        (string FileName, byte[] Content)[] files,
        CancellationToken cancellationToken = default)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var (fileName, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(part, "files", fileName);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = multipart };
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request, cancellationToken);
    }

    private Task<HttpResponseMessage> UploadAsync(string sessionId, params (string FileName, byte[] Content)[] files) =>
        UploadAsync(sessionId, files, CancellationToken.None);

    private async Task<HttpResponseMessage> AskAsync(string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent("""{ "question": "What are the payment terms?" }""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await _client.SendAsync(request);
    }

    private async Task<List<JsonElement>> ListDocumentsAsync(string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        request.Headers.Add(SessionHeader.Name, sessionId);
        return await ReadDocumentsAsync(await _client.SendAsync(request));
    }

    private static async Task<List<JsonElement>> ReadDocumentsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.GetProperty("documents").EnumerateArray().ToList();
    }

    /// <summary>The API with a controllable extractor, a scripted model and small limits.</summary>
    public sealed class ConcurrencyFactory : ApiFactory
    {
        public const int MaxParallelExtractions = 2;

        public ControllableExtractor Extractor { get; } = new();

        public ScriptedChatModelClient Model { get; } = new();

        protected override Dictionary<string, string?> GetSettings()
        {
            var settings = base.GetSettings();
            settings["Limits:MaxParallelExtractions"] = MaxParallelExtractions.ToString();
            settings["Limits:ExtractionTimeoutSeconds"] = "1";
            settings["Limits:MaxDocumentsPerSession"] = "5";
            settings["Limits:ChatRequestsPerMinute"] = "10000";
            return settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IDocumentExtractor>(Extractor);
                services.AddSingleton<IChatModelClient>(Model);
            });
        }
    }
}
