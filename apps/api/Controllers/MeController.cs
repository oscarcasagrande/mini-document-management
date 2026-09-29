using DocReader.Api.Contracts.V1;
using DocReader.Api.Security;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Controllers;

/// <summary>Identity of the caller of this request.</summary>
[ApiController]
[Route("api/v1/me")]
[Produces("application/json")]
public sealed class MeController : ControllerBase
{
    /// <summary>Returns the caller's identity.</summary>
    /// <remarks>
    /// When OIDC is configured (<c>OIDC_AUTHORITY</c> set), this endpoint carries the same global
    /// authentication requirement as every other controller: a request with no valid bearer token never
    /// reaches this action, and the global fallback policy answers 401 first. In anonymous mode there is
    /// no token to read, so this returns a fixed payload with every field empty instead of 401 — the same
    /// posture as the rest of this anonymous PoC.
    /// </remarks>
    /// <response code="200">The caller's identity, or the anonymous payload when OIDC is not configured.</response>
    [HttpGet]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public ActionResult<MeResponse> Get() => Ok(MeResponseMapper.Map(User));
}
