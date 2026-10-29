using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace DocumentReader.Api.Controllers;

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController(ISessionStore sessionStore) : ControllerBase
{
    /// <summary>Creates a new anonymous session. This is the only endpoint that needs no X-Session-Id.</summary>
    [HttpPost]
    [AllowWithoutSession]
    [ProducesResponseType<CreateSessionResponse>(StatusCodes.Status201Created)]
    public IActionResult Create()
    {
        var session = sessionStore.CreateSession();
        var response = new CreateSessionResponse(session.Id, session.CreatedAt.UtcDateTime);
        return StatusCode(StatusCodes.Status201Created, response);
    }
}
