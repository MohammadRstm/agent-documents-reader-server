using DocumentReader.Api.Errors;
using DocumentReader.Api.Ingestion;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace DocumentReader.Api.Controllers;

[ApiController]
[Route("api/evidence")]
public sealed class EvidenceController : ControllerBase
{
    /// <summary>Returns one evidence item of the current session. IDs from other sessions are not found.</summary>
    [HttpGet("{evidenceId}")]
    [ProducesResponseType<EvidenceDetail>(StatusCodes.Status200OK)]
    public IActionResult Get(string evidenceId)
    {
        var session = HttpContext.GetCurrentSession();
        if (!session.Documents.TryGetEvidence(evidenceId, out var evidence))
        {
            throw new ApiException(
                StatusCodes.Status404NotFound,
                ErrorCodes.EvidenceNotFound,
                "The evidence was not found in this session.");
        }

        return Ok(EvidenceDetail.From(evidence));
    }
}
