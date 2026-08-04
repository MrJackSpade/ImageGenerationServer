//TODO: CHECK FOR FALLBACKS
using ImageGen.Web.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// Whether a newer release of this application exists. JSON only — the banner builds itself from it.
///
/// <para>Every page asks this, so it must be cheap and it must never fail the page: the check runs once per
/// process and this hands back the answer it already has. A build with no version, a check that is turned off,
/// and a GitHub that did not answer all produce the same empty result, because to the person looking at the
/// page they are the same thing — nothing to report.</para>
/// </summary>
[Authorize]
[Route("/api/update")]
public sealed class UpdateController(UpdateCheck updates) : Controller
{
    private readonly UpdateCheck _updates = updates;

    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var status = await _updates.GetAsync(ct);
        return Json(new { current = status.Current, latest = status.Latest, url = status.Url });
    }
}
