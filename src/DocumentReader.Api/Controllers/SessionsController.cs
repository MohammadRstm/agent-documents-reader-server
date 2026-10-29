using DocumentReader.Api.Configuration;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocumentReader.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController(ISessionStore sessionStore) : ControllerBase
{
    /// <summary>Creates a new anonymous session. This is the only endpoint that needs no X-Session-Id.</summary>
    [HttpPost]
    [AllowWithoutSession]
    [EnableRateLimiting(RateLimitPolicies.SessionCreation)]
    [ProducesResponseType<CreateSessionResponse>(StatusCodes.Status201Created)]
    public IActionResult Create()
    {
        var session = sessionStore.CreateSession();
        var response = new CreateSessionResponse(session.Id, session.CreatedAt.UtcDateTime);
        return StatusCode(StatusCodes.Status201Created, response);
    }
}
