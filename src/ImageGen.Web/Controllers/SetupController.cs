using ImageGen.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// First boot: ask for the settings the box cannot run without, then get out of the way.
///
/// <para>Anonymous by necessity, not by choice. A fresh install has no accounts — registration is gated by
/// Auth:RegistrationCode, which is itself one of these settings — so there is nobody to authenticate as yet. It
/// stops answering the moment the required keys have values (see <see cref="SetupRequiredMiddleware"/>), which is
/// what keeps it from being a permanently open configuration endpoint on an internet-reachable box.</para>
/// </summary>
[AllowAnonymous]
public sealed class SetupController(MachineConfigService machine, ComfyProbe probe) : Controller
{
    private readonly MachineConfigService _machine = machine;
    private readonly ComfyProbe _probe = probe;

    [HttpGet("/setup")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        // Configured already: this page has nothing to ask. Sending people to the settings page rather than 404ing
        // means a bookmarked /setup still lands somewhere useful.
        if (_machine.IsConfigured) return Redirect("/settings/machine");

        // The box is pre-filled with the address a local ComfyUI almost always uses — so say whether that is a
        // real find or just the usual guess. A pre-filled field that looks authoritative and is not is worse than
        // an empty one: it invites a click-through and the failure surfaces at the first render instead.
        ViewData["Address"] = ComfyProbe.LikelyLocal;
        var found = await _probe.TryAsync(ComfyProbe.LikelyLocal, ct);
        ViewData["Detected"] = found.Ok;
        ViewData["ProbeError"] = found.Error;
        return View();
    }

    [HttpPost("/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string? baseUrl, bool useAnyway, CancellationToken ct)
    {
        if (_machine.IsConfigured) return Redirect("/settings/machine");

        ViewData["Address"] = baseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ViewData["Error"] = "Enter the address ComfyUI is listening on.";
            return View("Index");
        }

        // Check before storing, unless they have already been told and want it anyway — setting up before ComfyUI
        // is running is a legitimate thing to do, so this warns once and then believes them.
        if (!useAnyway)
        {
            var reachable = await _probe.TryAsync(baseUrl, ct);
            if (!reachable.Ok)
            {
                ViewData["Error"] = $"Nothing answered at that address — {reachable.Error}.";
                ViewData["OfferUseAnyway"] = true;
                return View("Index");
            }
        }

        await _machine.SetAsync(MachineSettingSpecs.ComfyBaseUrl, baseUrl, ct);
        return Redirect("/");
    }
}
