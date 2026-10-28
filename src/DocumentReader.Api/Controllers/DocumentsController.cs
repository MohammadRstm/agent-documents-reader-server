using DocumentReader.Api.Configuration;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocumentReader.Api.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(
    UploadFormReader uploadFormReader,
    DocumentIngestionService ingestionService) : ControllerBase
{
    /// <summary>
    /// Uploads one or more files (multipart parts named "files"). Returns one summary per file in
    /// upload order; per-file problems are reported through each summary's status and warnings.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Upload)]
    [DisableFormValueModelBinding]
    [ProducesResponseType<DocumentListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        var session = HttpContext.GetCurrentSession();
        var files = await uploadFormReader.ReadFilesAsync(Request, cancellationToken);
        var documents = await ingestionService.IngestAsync(session, files, cancellationToken);
        return Ok(ToResponse(documents));
    }

    /// <summary>Lists all documents in the session, in upload order.</summary>
    [HttpGet]
    [ProducesResponseType<DocumentListResponse>(StatusCodes.Status200OK)]
    public IActionResult List()
    {
        var session = HttpContext.GetCurrentSession();
        return Ok(ToResponse(session.Documents.GetDocuments()));
    }

    private static DocumentListResponse ToResponse(IEnumerable<DocumentRecord> documents) =>
        new(documents.Select(DocumentSummary.From).ToList());
}
