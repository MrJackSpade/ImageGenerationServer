using ImageGen.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ImageGen.Web.Controllers;

/// <summary>
/// Reads and writes this box's configuration. JSON only — the page builds its own DOM from it.
///
/// <para>Every key is checked against <see cref="MachineSettingSpecs"/>, so this cannot be used to write arbitrary
/// configuration into the process: an unknown key is a 400, not a new setting.</para>
///
/// <para>There are no roles in this app, so any signed-in user can change these. That is a real property of the
/// install and the settings page says so out loud rather than hiding it behind a page that looks personal.</para>
/// </summary>
[Authorize]
[Route("/api/machine-settings")]
public sealed class MachineSettingsController(MachineConfigService machine, ComfyProbe probe) : Controller
{
    private readonly MachineConfigService _machine = machine;
    private readonly ComfyProbe _probe = probe;

    public sealed record SettingWrite(string Key, string? Value);
    public sealed record ProbeRequest(string? Url);

    [HttpGet("")]
    public IActionResult Get() => Json(new
    {
        machineName = _machine.MachineName,
        configured = _machine.IsConfigured,
        overrideFile = _machine.OverrideFilePath,
        settings = _machine.Describe(),
    });

    [HttpPut("")]
    public async Task<IActionResult> Put([FromBody] SettingWrite body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Key))
        {
            return BadRequest(new { error = "A key is required." });
        }

        MachineSettingSpec? spec = MachineSettingSpecs.Find(body.Key);
        if (spec is null)
        {
            return BadRequest(new { error = $"'{body.Key}' is not a machine setting." });
        }

        if (spec.Required && string.IsNullOrWhiteSpace(body.Value))
        {
            return BadRequest(new { error = $"{spec.Label} is required and cannot be cleared." });
        }

        await _machine.SetAsync(spec.Key, body.Value, ct);
        return Json(new { ok = true, live = spec.Apply == SettingApply.Live, value = body.Value });
    }

    /// <summary>
    /// Ask an address whether ComfyUI is behind it. "Which box answers" is the fact worth knowing — a stored value
    /// can be present and wrong, and no is-it-null check will ever catch that.
    /// </summary>
    [HttpPost("probe")]
    public async Task<IActionResult> Probe([FromBody] ProbeRequest body, CancellationToken ct)
    {
        ProbeResult result = await _probe.TryAsync(body?.Url, ct);
        return Json(new { ok = result.Ok, error = result.Error });
    }
}
