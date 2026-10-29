# API Contract (v1)

Shared contract between `agent-documents-reader-server` (ASP.NET Core) and `agent-documents-reader-client` (React). Both repos keep an identical copy at `docs/api-contract.md`. Any change must be made in both repos in the same step.

## Conventions

- Base path: `/api`. Dev server: `http://localhost:5080`. Vite dev server (`http://localhost:5173`) proxies `/api` to it.
- JSON uses camelCase property names. Enums are camelCase strings. Timestamps are ISO 8601 UTC strings.
- Every endpoint except `POST /api/sessions` requires the header `X-Session-Id`.
- Evidence IDs have the form `E<positive integer>` (e.g. `E12`) and are unique within a session.
- Citation markers inside answer text have the exact form `[E12]`. Adjacent markers are written `[E3][E7]`. Regex: `\[E\d+\]`.

## Error response

Every non-2xx JSON response has this body:

```json
{ "code": "session_not_found", "message": "Safe human-readable message.", "traceId": "00-abc...", "details": { "question": ["Question must not be empty."] } }
```

`details` is optional and only present for `validation_failed`.

| HTTP | code | When |
|---|---|---|
| 400 | `validation_failed` | Invalid body, fields or query |
| 400 | `session_required` | `X-Session-Id` header missing or malformed |
| 404 | `session_not_found` | Unknown or expired session |
| 404 | `evidence_not_found` | Evidence ID not found in this session |
| 409 | `question_in_progress` | A chat answer is already streaming for this session |
| 409 | `documents_processing` | A question was sent while documents in this session are still being processed |
| 409 | `no_documents` | A question was sent but the session has no readable documents |
| 413 | `payload_too_large` | Request body or batch exceeds limits |
| 415 | `unsupported_media_type` | Request is not `multipart/form-data` (upload) or JSON (chat) |
| 429 | `rate_limited` | Rate limit exceeded |
| 502 | `provider_unavailable` | The LLM provider failed or timed out |
| 500 | `internal_error` | Unexpected failure |

## Types

```ts
type FileType = "pdf" | "xlsx" | "xlsm" | "xls" | "unknown";

type DocumentStatus =
  | "indexed"           // fully extracted
  | "partiallyIndexed"  // some pages/sheets failed or limits truncated content; see warnings
  | "noTextExtracted"   // PDF with no usable text (likely scanned)
  | "empty"             // valid file with no content
  | "unsupported"       // file signature/extension not a supported type
  | "corrupt"           // recognized type but unreadable or encrypted
  | "tooLarge";         // over the per-file size limit, or too large to process once uncompressed (Excel)

interface SheetSummary {
  name: string;
  tableCount: number;
  rowCount: number;        // non-empty rows in the sheet
}

interface DocumentSummary {
  id: string;              // server-generated, opaque
  fileName: string;        // original name, display only
  fileType: FileType;
  sizeBytes: number;
  status: DocumentStatus;
  uploadedAt: string;
  pageCount: number | null;        // PDFs only
  sheets: SheetSummary[] | null;   // Excel only
  tableCount: number;
  chunkCount: number;
  warnings: string[];              // safe, user-facing
}

type SourceLocation =
  | { type: "pdfPage"; page: number }                    // 1-based
  | { type: "excelRange"; sheet: string; range: string }; // A1 notation, e.g. "A5:F5"

interface Citation {
  evidenceId: string;
  documentId: string;
  fileName: string;
  source: SourceLocation;
  snippet: string;         // <= 300 chars
}

interface EvidenceDetail {
  evidenceId: string;
  documentId: string;
  fileName: string;
  kind: "textChunk" | "tableRow";
  source: SourceLocation;
  text: string;            // <= 4000 chars
}

interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;         // assistant content contains only validated [E#] markers
  createdAt: string;
  citations: Citation[];   // empty for user messages
  incomplete: boolean;     // true if the agent hit a limit; always false for user messages
}
```

## Endpoints

### `POST /api/sessions`
No body. `201 Created`:
```json
{ "sessionId": "<43-char base64url>", "createdAt": "2026-10-28T12:00:00Z" }
```

### `POST /api/documents`
`multipart/form-data`, one or more parts named `files`. `200 OK` with one entry per uploaded file, in upload order:
```json
{ "documents": [DocumentSummary] }
```
Per-file problems (unsupported, corrupt, tooLarge, ...) are reported through `status` and `warnings` with `200`. Whole-request problems (no files, too many files, total size over limit) return an error response.

Clients may send several upload requests for the same session in parallel; each request is processed independently. Files are parsed concurrently, bounded server-wide by configuration. While any upload request for a session is still processing, `POST /api/chat` returns `409 documents_processing`.

### `GET /api/documents`
`200 OK`: `{ "documents": [DocumentSummary] }` (all documents in the session, upload order).

### `GET /api/evidence/{evidenceId}`
`200 OK`: `EvidenceDetail`. `404 evidence_not_found` if the ID is not in this session.

### `POST /api/chat`
JSON body: `{ "question": string }` (1 to 4000 characters after trimming).

Validation, session, `documents_processing`, `no_documents` and `question_in_progress` errors are returned as normal JSON error responses **before** the stream starts. Otherwise the response is `200` with `Content-Type: text/event-stream`. Each event is:

```
event: <name>
data: <single-line JSON>

```

| event | data | Notes |
|---|---|---|
| `status` | `{ "message": string, "tool": string \| null }` | Progress, e.g. "Searching documents…" |
| `delta` | `{ "text": string }` | Append to the draft answer. May contain partial or not-yet-validated markers. |
| `citations` | `{ "citations": Citation[] }` | Validated citations, sent once, just before `done` |
| `done` | `{ "messageId": string, "answer": string, "incomplete": boolean }` | Final authoritative answer text. Replaces the streamed draft. Stream ends. |
| `error` | `{ "code": string, "message": string, "traceId": string }` | Stream ends. No `done` follows. |

### `GET /api/chat/history`
`200 OK`: `{ "messages": [ChatMessage] }` (oldest first).
