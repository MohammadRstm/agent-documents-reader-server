# Document Reader API

ASP.NET Core Web API (.NET 10) for the AI document-analysis assessment. Users upload PDF and Excel documents into an anonymous session and ask questions about them; answers cite the source page or cell range.

Status: work in progress. Implemented so far: sessions, the shared error format, rate limiting, PDF and Excel upload and extraction, keyword search, the agent tools and evidence lookup (`GET /api/evidence/{id}`). Chat is not implemented yet.

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
| `Limits` | `MaxFailedDocumentsPerSession` | 50 | Failed files kept per session; beyond this they are reported in the upload response but not stored |
| `Limits` | `MaxPdfPages` | 500 | Later pages are skipped; status `partiallyIndexed` |
| `Limits` | `ExtractionTimeoutSeconds` | 30 | Time budget for extracting one file |
| `Limits` | `MaxUncompressedWorkbookBytes` | 104857600 (100 MB) | .xlsx/.xlsm larger than this once uncompressed get `tooLarge` |
| `Limits` | `MaxWorkbookPackageEntries` | 10000 | Parts allowed inside an .xlsx/.xlsm package |
| `Limits` | `MaxWorkbookCompressionRatio` | 100 | Highest compression ratio for a package part over `WorkbookCompressionRatioCheckBytes` |
| `Limits` | `WorkbookCompressionRatioCheckBytes` | 1048576 (1 MB) | Smaller parts skip the ratio check |
| `Limits` | `MaxSheetsPerWorkbook` | 100 | Later sheets are skipped |
| `Limits` | `MaxCellsPerWorkbook` | 1000000 | Later cells are skipped |
| `Limits` | `MaxExcelRows` / `MaxExcelColumns` | 100000 / 512 | Cells beyond these are skipped |
| `Limits` | `MaxCellTextLength` | 2000 | Longer cell text is cut off |
| `Limits` | `MaxMergedRangeCells` | 10000 | Larger merged ranges are not spread over their cells |
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
- Before an .xlsx/.xlsm is opened, its ZIP structure is checked against `MaxUncompressedWorkbookBytes`, `MaxWorkbookPackageEntries` and `MaxWorkbookCompressionRatio`; a failing package gets status `tooLarge`. Reading also stops at `MaxSheetsPerWorkbook`, `MaxCellsPerWorkbook`, `MaxExcelRows` or `MaxExcelColumns` (status `partiallyIndexed`).
- Every sheet is scanned for tables, without assuming a fixed layout:
  - Headers can be offset (they need not start at A1) and can span two rows with a merged group label.
  - Repeated header rows are skipped, and two or more blank rows end a table.
  - Rows are classified as item, section header, subtotal, total or unknown.
  - Columns get a type and, when the file states one consistently, a currency.
  - Ambiguities are reported in `warnings`.
- Each table row becomes one evidence chunk labelled with its column headers, cited by sheet and cell range (for example `B5:G5`). Rows outside tables (titles, notes) are grouped into text chunks.
- Password-protected Office files and unreadable workbooks get status `corrupt`.
- Uploaded bytes are held in memory only while a file is processed. ASP.NET Core may buffer large multipart parts to temporary files during the request; it deletes them when the request ends. Extracted text lives in the session's memory.

## Search and agent tools

The backend gives the model seven read-only tools (`src/DocumentReader.Api/Agent/Tools`). They are plain services that can be called and tested without an LLM:

| Tool | Purpose |
|---|---|
| `list_documents` | Documents, status, sheets and detected tables (table IDs `T1`, `T2`, ...) |
| `search_text` | BM25 keyword search over all evidence; best matches only, not exhaustive |
| `get_evidence` | Full text and source of up to 10 evidence items |
| `describe_table` | Columns (type, currency), row-kind counts, sample rows, warnings |
| `query_table` | Filters and sorts every row of a table; exact match count |
| `aggregate_table` | Sum, count, average, min or max with decimal arithmetic. Only item rows are included by default (no subtotals or totals), and values in different currencies are never combined |
| `find_dates` | Scans all evidence for explicit dates; ambiguous dates such as 03/04/2027 are returned with both readings |

Tool arguments are validated strictly in code: unknown properties, wrong types, out-of-range values and unknown IDs are rejected with an error result. Document, table and evidence IDs are only resolved inside the caller's session. Results are size-limited.

## Sessions

`POST /api/sessions` returns a 43-character random session ID. Every other endpoint requires it in the `X-Session-Id` header. Sessions and all their data live in memory only: they are removed after the idle timeout and lost when the process restarts. A session ID is an unguessable bearer value, not user authentication.

## Tests

```bash
dotnet test tests/DocumentReader.Tests/DocumentReader.Tests.csproj
```

The tests are deterministic and need no API key or network access. Legacy .xls fixtures are generated with NPOI, a test-only dependency whose binary package requires accepting its OSMF EULA (accepted by the project owner, see `tests/DocumentReader.Tests/DocumentReader.Tests.csproj`). CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, and runs this test project only.
