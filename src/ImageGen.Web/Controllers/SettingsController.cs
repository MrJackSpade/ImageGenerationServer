using ImageGen.Web.Comfy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// Settings is an index of the settings pages, not a page of settings. What sits under it falls into two kinds that
/// must not be confused: things scoped to the signed-in account (bans, and the workflow library's favourites, tags
/// and hidden list), and things that are properties of THIS BOX and therefore the same for everyone with an account
/// (which file fills a catalogue slot, and the box's own configuration). The index says which is which, because
/// nothing else does — there are no roles here, so any signed-in user can change the machine-wide ones.
/// </summary>
[Authorize]
public sealed class SettingsController : Controller
{
    [HttpGet("/settings")]
    public IActionResult Index() => View();

    /// <summary>Per-workflow banned tags and artists — account-scoped.</summary>
    [HttpGet("/settings/bans")]
    public IActionResult Bans() => View();

    /// <summary>This box: its configuration, and the actions that act on the renderer itself.</summary>
    [HttpGet("/settings/machine")]
    public IActionResult Machine([FromServices] ComfySupervisor supervisor)
    {
        // The Restart ComfyUI button only makes sense where this deployment supervises the renderer (the Docker
        // image) and it is running — the same gate the patches page uses. In the plain release build there is no
        // supervisor, so the button is not rendered.
        ViewData[ViewDataKeys.CanRestartComfy] = supervisor.CanRestart;
        return View();
    }

    /// <summary>What this app changes in ComfyUI's own code, and whether those changes are in place.</summary>
    [HttpGet("/settings/patches")]
    public IActionResult Patches() => View();

    /// <summary>Keys under which this controller passes values to its view.</summary>
    private static class ViewDataKeys
    {
        /// <summary>Whether this deployment supervises ComfyUI and can restart it.</summary>
        public const string CanRestartComfy = "CanRestartComfy";
    }
}
