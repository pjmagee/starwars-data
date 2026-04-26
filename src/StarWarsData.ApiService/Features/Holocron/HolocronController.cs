using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StarWarsData.Models;
using StarWarsData.Services.AI.Agents;

namespace StarWarsData.ApiService.Features.Holocron;

/// <summary>
/// User-facing endpoints for the Holocron agent. The Frontend's node detail page
/// surfaces an "Enhance with Holocron" button for admin users — this controller
/// is what that button hits.
///
/// All write endpoints are admin-gated via the <c>X-User-Roles</c> header that
/// the Frontend sets from the authenticated <see cref="System.Security.Claims.ClaimsPrincipal"/>
/// (see ADR-001). The agent always stamps <c>triggeredBy: "manual"</c> on the
/// resulting events so the changelog distinguishes them from scheduled-pass output.
/// </summary>
[ApiController]
[Route("api/holocron")]
[Produces("application/json")]
public class HolocronController(HolocronAgent agent, IOptions<SettingsOptions> settings) : ControllerBase
{
    /// <summary>
    /// Synchronous single-node enhancement. Bypasses the daily-pass selection logic —
    /// you've explicitly chosen this node. Returns a summary of how many enrichments
    /// were created and how many proposals failed evidence validation.
    ///
    /// Requires the caller to be an admin (<c>X-User-Roles</c> contains <c>admin</c>).
    /// Honours <see cref="SettingsOptions.HolocronEnabled"/> as a kill switch — when
    /// false, returns 503 without contacting the LLM.
    /// </summary>
    [HttpPost("enhance/{pageId:int}")]
    public async Task<ActionResult<NodeEnhancementSummary>> EnhanceNode(int pageId, CancellationToken ct)
    {
        if (!IsAdmin())
            return Forbid();

        if (!settings.Value.HolocronEnabled)
            return StatusCode(503, new { error = "Holocron is disabled (Settings.HolocronEnabled = false)." });

        var summary = await agent.EnhanceNodeAsync(pageId, "manual", ct);
        return Ok(summary);
    }

    bool IsAdmin() => Request.Headers["X-User-Roles"].FirstOrDefault()?.Split(',', StringSplitOptions.TrimEntries).Contains("admin", StringComparer.OrdinalIgnoreCase) ?? false;
}
