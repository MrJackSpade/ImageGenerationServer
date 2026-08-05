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
        if (_machine.IsConfigured) return Redirect(Routes.MachineSettings);

        // The box is pre-filled with the address a local ComfyUI almost always uses — so say whether that is a
        // real find or just the usual guess. A pre-filled field that looks authoritative and is not is worse than
        // an empty one: it invites a click-through and the failure surfaces at the first render instead.
        ViewData[ViewDataKeys.Address] = ComfyProbe.Addresses.LikelyLocal;
        ProbeResult found = await _probe.TryAsync(ComfyProbe.Addresses.LikelyLocal, ct);
        ViewData[ViewDataKeys.Detected] = found.Ok;
        ViewData[ViewDataKeys.ProbeError] = found.Error;
        return View();
    }

    [HttpPost("/setup")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string? baseUrl, bool useAnyway, CancellationToken ct)
    {
        if (_machine.IsConfigured) return Redirect(Routes.MachineSettings);

        ViewData[ViewDataKeys.Address] = baseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ViewData[ViewDataKeys.Error] = "Enter the address ComfyUI is listening on.";
            return View(Views.Index);
        }

        // Check before storing, unless they have already been told and want it anyway — setting up before ComfyUI
        // is running is a legitimate thing to do, so this warns once and then believes them.
        if (!useAnyway)
        {
            ProbeResult reachable = await _probe.TryAsync(baseUrl, ct);
            if (!reachable.Ok)
            {
                ViewData[ViewDataKeys.Error] = $"Nothing answered at that address — {reachable.Error}.";
                ViewData[ViewDataKeys.OfferUseAnyway] = true;
                return View(Views.Index);
            }
        }

        await _machine.SetAsync(MachineSettingSpecs.Keys.ComfyBaseUrl, baseUrl, ct);
        return Redirect(Routes.Home);
    }

    /// <summary>View names this controller renders.</summary>
    private static class Views
    {
        /// <summary>The setup form.</summary>
        public const string Index = "Index";
    }

    /// <summary>Local routes this controller redirects to.</summary>
    private static class Routes
    {
        /// <summary>The machine settings page, shown once the box is already configured.</summary>
        public const string MachineSettings = "/settings/machine";

        /// <summary>The application home page.</summary>
        public const string Home = "/";
    }

    /// <summary>Keys under which this controller passes values to its view.</summary>
    private static class ViewDataKeys
    {
        /// <summary>The ComfyUI address shown in the form field.</summary>
        public const string Address = "Address";

        /// <summary>Whether a ComfyUI instance was detected at the likely-local address.</summary>
        public const string Detected = "Detected";

        /// <summary>The error returned while probing the likely-local address, if any.</summary>
        public const string ProbeError = "ProbeError";

        /// <summary>The message shown when the entered address is missing or unreachable.</summary>
        public const string Error = "Error";

        /// <summary>Whether to offer saving the address anyway after a failed reachability check.</summary>
        public const string OfferUseAnyway = "OfferUseAnyway";
    }
}
