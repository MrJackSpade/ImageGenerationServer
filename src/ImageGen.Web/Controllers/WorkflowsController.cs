using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// The Workflows library: an index of every runnable workflow and a per-workflow page. Both are JS-driven shells —
/// the data comes from /forge/workflows (catalog + avgSeconds + sizeBytes), /api/settings (favorites + custom tags),
/// and /api/history?workflow= (the workflow's recents) — so these actions just render the shell.
///
/// They live under /settings because that is where they are reached from: the rail carries one Settings entry rather
/// than one per library. The old top-level addresses redirect permanently rather than 404 — every saved image's
/// detail card links its workflow by name, so those URLs are in browser history and in people's bookmarks.
/// </summary>
[Authorize]
public sealed class WorkflowsController : Controller
{
    [HttpGet(Routes.WorkflowsIndex)]
    public IActionResult Index() => View();

    /// <summary>
    /// The models page: which file on this machine fills each catalogue slot, and which workflows that leaves
    /// unavailable. A JS shell over /forge/catalog/*.
    /// </summary>
    [HttpGet(Routes.ModelsIndex)]
    public IActionResult Models() => View();

    /// <summary>The LoRA manager: every LoRA on this box with its cover, CivitAI-fetched trigger words (editable), and
    /// whether those words auto-attach to the prompt. A JS shell over /forge/loras/manage + /api/lora/*.</summary>
    [HttpGet("/settings/loras")]
    public IActionResult Loras() => View();

    [HttpGet("/settings/workflows/{id}")]
    public IActionResult Detail(string id)
    {
        ViewData[ViewDataKeys.WorkflowId] = id;
        return View();
    }

    [HttpGet("/workflows")]
    public IActionResult IndexMoved() => RedirectPermanent(Routes.WorkflowsIndex);

    [HttpGet("/models")]
    public IActionResult ModelsMoved() => RedirectPermanent(Routes.ModelsIndex);

    [HttpGet("/workflow/{id}")]
    public IActionResult DetailMoved(string id) => RedirectPermanent($"/settings/workflows/{Uri.EscapeDataString(id)}");

    /// <summary>Current routes the old top-level addresses redirect to.</summary>
    private static class Routes
    {
        /// <summary>The workflow library index.</summary>
        public const string WorkflowsIndex = "/settings/workflows";

        /// <summary>The models page.</summary>
        public const string ModelsIndex = "/settings/models";
    }

    /// <summary>Keys under which this controller passes values to its view.</summary>
    private static class ViewDataKeys
    {
        /// <summary>The id of the workflow whose detail page is being rendered.</summary>
        public const string WorkflowId = "WorkflowId";
    }
}
