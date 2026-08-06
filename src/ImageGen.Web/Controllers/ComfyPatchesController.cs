using ImageGen.Comfy.Patches;
using ImageGen.Web.Comfy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// The patches page's own backing. JSON only — the page builds its DOM from it.
///
/// <para>A patch is addressed by its id and nothing else: no route here takes a path, a diff or a file, so this
/// cannot be used to write arbitrary content into the ComfyUI directory. An id that is not in this build's patch
/// set is a 400.</para>
///
/// <para>There are no roles in this app, so any signed-in user can change these — the same property the rest of
/// the machine-wide settings have, and the page says so out loud rather than looking personal.</para>
/// </summary>
[Authorize]
[Route("/api/comfy-patches")]
public sealed class ComfyPatchesController(ComfyPatchService patches) : Controller
{
    private readonly ComfyPatchService _patches = patches;

    /// <summary>Which patch, and whether the caller has agreed to lose what applying it would overwrite.</summary>
    public sealed record PatchRequest(string? Id, bool Overwrite);

    [HttpGet("")]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        await GuardAsync(async () => Json(await _patches.DescribeAsync(ct)));

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] PatchRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Id))
        {
            return BadRequest(new { error = "A patch id is required." });
        }

        return await GuardAsync(async () => Json(new { ok = true, note = await _patches.ApplyAsync(body.Id, body.Overwrite, ct) }));
    }

    [HttpPost("apply-all")]
    public async Task<IActionResult> ApplyAll(CancellationToken ct) =>
        await GuardAsync(async () => Json(new { ok = true, notes = await _patches.ApplyAllAsync(ct) }));

    [HttpPost("remove")]
    public IActionResult Remove([FromBody] PatchRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.Id))
        {
            return BadRequest(new { error = "A patch id is required." });
        }

        return Guard(() =>
        {
            _patches.Remove(body.Id);
            return Json(new { ok = true });
        });
    }

    /// <summary>
    /// Restart the renderer so it re-reads what the patches changed. Only where this deployment supervises it —
    /// elsewhere the page shows a note instead of a button, and this answers 400 if it is called anyway.
    /// </summary>
    [HttpPost("restart")]
    public IActionResult Restart() => Guard(() =>
    {
        _patches.Restart();
        return Json(new { ok = true });
    });

    private IActionResult Guard(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsSomethingToTellTheOperator(ex))
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (IsSomethingToTellTheOperator(ex))
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deliberately narrow. A conflict, an unreadable patch file, a misconfigured renderer folder and an
    /// unsupported restart are all things to TELL the operator, in the words the exception already uses.
    /// Anything else is a bug and belongs in the global handler with its stack trace intact.
    /// </summary>
    private static bool IsSomethingToTellTheOperator(Exception ex) => ex
        is PatchConflictException
        or PackSource.FetchException
        or ComfyPatchCatalog.LoadException
        or UnifiedDiff.FormatException
        or InvalidOperationException
        or PlatformNotSupportedException;
}
