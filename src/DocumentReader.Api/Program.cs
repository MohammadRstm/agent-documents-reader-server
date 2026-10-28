using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Errors;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Ingestion.Excel;
using DocumentReader.Api.Ingestion.Pdf;
using DocumentReader.Api.Llm;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Configuration, validated at startup so misconfiguration fails fast.
builder.Services.AddOptions<LimitsOptions>()
    .Bind(builder.Configuration.GetSection(LimitsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<SessionLifetimeOptions>()
    .Bind(builder.Configuration.GetSection(SessionLifetimeOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// JSON: camelCase properties (the default) and camelCase enum strings, both for MVC and for
// responses written outside MVC (exception handler, rate limiter).
static void ConfigureJson(JsonSerializerOptions options) =>
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

builder.Services.ConfigureHttpJsonOptions(options => ConfigureJson(options.SerializerOptions));
builder.Services
    .AddControllers(options => options.Filters.Add<RequireSessionFilter>())
    .AddJsonOptions(options =>
    {
        ConfigureJson(options.JsonSerializerOptions);
        // Do not echo serializer exception text (type names, internals) back to clients.
        options.AllowInputFormatterExceptionMessages = false;
    })
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = ValidationErrorResponseFactory.Create);

// Errors
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Sessions
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddHostedService<SessionCleanupService>();

// Ingestion
builder.Services.AddOptions<FormOptions>()
    .Configure<IOptions<LimitsOptions>>((form, limits) =>
        form.MultipartBodyLengthLimit = limits.Value.MaxUploadRequestBytes);
builder.Services.AddSingleton<UploadFormReader>();
builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddSingleton<XlsxGridReader>();
builder.Services.AddSingleton<XlsGridReader>();
builder.Services.AddSingleton<ExcelDocumentExtractor>();
builder.Services.AddSingleton<DocumentIngestionService>();

builder.Services.AddApiRateLimiting();

var app = builder.Build();

app.UseExceptionHandler();
app.UseRouting();
app.UseRateLimiter();
app.MapControllers();

app.Run();
