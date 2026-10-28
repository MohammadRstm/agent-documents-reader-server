# Document Reader API

ASP.NET Core Web API (.NET 10) for the AI document-analysis assessment. Users upload PDF and Excel documents into an anonymous session and ask questions about them; answers cite the source page or cell range.

Status: work in progress. Implemented so far: sessions, the shared error format, rate limiting, and PDF and Excel upload and extraction. Search, evidence lookup and chat are not implemented yet.

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
| `Limits` | `SessionCreationRequestsPerMinute` | 10 | Sessions created per client IP per minute |
| `Limits` | `UploadRequestsPerMinute` | 10 | Upload requests per client IP per minute |
| `Limits` | `ChatRequestsPerMinute` | 20 | Chat questions per client IP per minute |
| `Limits` | `MaxFileSizeBytes` | 26214400 (25 MB) | Larger files get status `tooLarge` and are not processed |
| `Limits` | `MaxFilesPerUpload` | 10 | More files in one request rejects the request (413) |
| `Limits` | `MaxUploadRequestBytes` | 104857600 (100 MB) | Larger upload requests are rejected (413) |
| `Limits` | `MaxDocumentsPerSession` | 20 | Readable documents (indexed or partially indexed) per session; uploads that would exceed it are rejected (413). Failed files are listed but do not count |
| `Limits` | `MaxPdfPages` | 500 | Later pages are skipped; status `partiallyIndexed` |
| `Limits` | `ExtractionTimeoutSeconds` | 30 | Time budget for extracting one file |
| `Limits` | `MaxModelRoundTripsPerQuestion` | 8 | Agent model round trips per question |
| `Limits` | `QuestionTimeoutSeconds` | 120 | Time budget per question |
| `Sessions` | `IdleTimeoutMinutes` | 60 | Session expires after this long without a request |
| `Sessions` | `CleanupIntervalSeconds` | 60 | How often expired sessions are removed |
| `Anthropic` | `Model` | `claude-sonnet-5-5` | Claude model ID |
| `Anthropic` | `Effort` | `medium` | Model effort level |

## Document upload

`POST /api/documents` takes `multipart/form-data` with one or more parts named `files`; `GET /api/documents` lists the session's documents in upload order.

- The file type is detected from the file's content (magic bytes), never from its name or the client's content type. Supported: PDF, .xlsx, .xlsm and .xls.
- Each file is processed independently. Problems with one file are reported through its `status` and `warnings` and do not fail the other files.
- PDFs are read page by page with PdfPig. Text is split into chunks of up to 1500 characters, each tied to its 1-based page number and given a session-unique evidence ID (`E1`, `E2`, ...).
- PDFs that contain only images (likely scanned) get status `noTextExtracted`; OCR is not supported. Password-protected or unreadable PDFs get status `corrupt`. A file over the size limit gets `tooLarge`, and an empty file gets `empty`.
- Excel workbooks are read with ClosedXML (.xlsx, .xlsm) or ExcelDataReader (.xls) into one shared cell model. Formula cells use the value saved in the file; formulas are never recalculated and macros are never run. A formula with no saved value is treated as empty and reported in `warnings`. .xls files provide no formatting (bold) hints.
- Before an .xlsx/.xlsm is opened, its ZIP structure is checked: more than 100 MB uncompressed, more than 10,000 parts, or a part compressed more than 100:1 gives status `tooLarge`. Reading also stops at 100 sheets, 1,000,000 cells, row 100,000 or column 512 (status `partiallyIndexed`).
- Every sheet is scanned for tables, without assuming a fixed layout:
  - Headers can be offset (they need not start at A1) and can span two rows with a merged group label.
  - Repeated header rows are skipped, and two or more blank rows end a table.
  - Rows are classified as item, section header, subtotal, total or unknown.
  - Columns get a type and, when the file states one consistently, a currency.
  - Ambiguities are reported in `warnings`.
- Each table row becomes one evidence chunk labelled with its column headers, cited by sheet and cell range (for example `B5:G5`). Rows outside tables (titles, notes) are grouped into text chunks.
- Password-protected Office files and unreadable workbooks get status `corrupt`.
- Uploaded bytes are held in memory only while a file is processed. ASP.NET Core may buffer large multipart parts to temporary files during the request; it deletes them when the request ends. Extracted text lives in the session's memory.

## Sessions

`POST /api/sessions` returns a 43-character random session ID. Every other endpoint requires it in the `X-Session-Id` header. Sessions and all their data live in memory only: they are removed after the idle timeout and lost when the process restarts. A session ID is an unguessable bearer value, not user authentication.

## Tests

```bash
dotnet test tests/DocumentReader.Tests/DocumentReader.Tests.csproj
```

The tests are deterministic and need no API key or network access. CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, and runs this test project only.
