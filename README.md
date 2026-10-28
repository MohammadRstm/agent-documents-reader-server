# Document Reader API

ASP.NET Core Web API (.NET 10) for the AI document-analysis assessment. Users upload PDF and Excel documents into an anonymous session and ask questions about them; answers cite the source page or cell range.

Status: work in progress. Implemented so far: session creation, `X-Session-Id` resolution with idle expiry, the shared error format, and rate-limiting policies. Document upload, search and chat are not implemented yet.

The API contract shared with the React client is in [`docs/api-contract.md`](docs/api-contract.md).

## Prerequisites

- .NET SDK 10.0 (developed with 10.0.402)

## Build and run

```bash
dotnet restore DocumentReader.sln
dotnet build DocumentReader.sln
dotnet run --project src/DocumentReader.Api --launch-profile http
```

The API listens on `http://localhost:5080`.

## Claude API key

The key is read from configuration as `Anthropic:ApiKey`. For local development store it in .NET User Secrets (never in `appsettings.json`):

```bash
dotnet user-secrets set "Anthropic:ApiKey" "<your-key>" --project src/DocumentReader.Api
```

User Secrets are only loaded in the `Development` environment. Elsewhere, use the environment variable `Anthropic__ApiKey`.

## Configuration

`src/DocumentReader.Api/appsettings.json`, validated at startup:

| Section | Setting | Default | Meaning |
|---|---|---|---|
| `Limits` | `UploadRequestsPerMinute` | 10 | Upload requests per client IP per minute |
| `Limits` | `ChatRequestsPerMinute` | 20 | Chat questions per client IP per minute |
| `Limits` | `MaxModelRoundTripsPerQuestion` | 8 | Agent model round trips per question |
| `Limits` | `QuestionTimeoutSeconds` | 120 | Time budget per question |
| `Sessions` | `IdleTimeoutMinutes` | 60 | Session expires after this long without a request |
| `Sessions` | `CleanupIntervalSeconds` | 60 | How often expired sessions are removed |
| `Anthropic` | `Model` | `claude-sonnet-5-5` | Claude model ID |
| `Anthropic` | `Effort` | `medium` | Model effort level |

## Sessions

`POST /api/sessions` returns a 43-character random session ID. Every other endpoint requires it in the `X-Session-Id` header. Sessions and all their data live in memory only: they are removed after the idle timeout and lost when the process restarts. A session ID is an unguessable bearer value, not user authentication.

## Tests

```bash
dotnet test tests/DocumentReader.Tests/DocumentReader.Tests.csproj
```

The tests are deterministic and need no API key or network access. CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, and runs this test project only.
