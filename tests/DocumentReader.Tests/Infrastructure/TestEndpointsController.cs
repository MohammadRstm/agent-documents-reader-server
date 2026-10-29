using System.ComponentModel.DataAnnotations;
using DocumentReader.Api.Configuration;
using DocumentReader.Api.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocumentReader.Tests.Infrastructure;

/// <summary>
/// Endpoints that exist only in tests, used to exercise cross-cutting behaviour (session
/// resolution, error handling, rate limiting) independently of the real feature endpoints.
/// </summary>
[ApiController]
[Route("api/test")]
public sealed class TestEndpointsController : ControllerBase
{
    public const string SecretExceptionText = @"C:\secret\path leaked-detail";

    [HttpGet("session")]
    public IActionResult GetSession() => Ok(new { sessionId = HttpContext.GetCurrentSession().Id });

    [HttpGet("throw")]
    [AllowWithoutSession]
    public IActionResult Throw() => throw new InvalidOperationException(SecretExceptionText);

    [HttpPost("validate")]
    [AllowWithoutSession]
    public IActionResult Validate(ValidatedBody body) => Ok(body);

    [HttpGet("limited")]
    [AllowWithoutSession]
    [EnableRateLimiting(RateLimitPolicies.Chat)]
    public IActionResult Limited() => Ok();

    public sealed class ValidatedBody
    {
        [Required(ErrorMessage = "Question is required.")]
        [StringLength(10, ErrorMessage = "Question is too long.")]
        public string? Question { get; set; }
    }
}
