//TODO: CHECK FOR FALLBACKS
using ImageGen.Comfy;

namespace ImageGen.Web.Configuration;

/// <summary>
/// <see cref="IComfyEndpoint"/> over live configuration. Reads on every access — that is the point of it, and the
/// reason the renderer can be re-pointed from the settings page without restarting the app.
///
/// <para>This lives in the composition root because it is the only place that knows what a configuration key is
/// called. The Comfy adapter takes the port and never sees a key name.</para>
/// </summary>
public sealed class ConfiguredComfyEndpoint(IConfiguration configuration) : IComfyEndpoint
{
    private readonly IConfiguration _configuration = configuration;

    /// <summary>
    /// No default. An unset address is a box nobody has configured, which is a question for the setup page — not
    /// something to paper over with a guess at localhost that then fails at the first render.
    /// </summary>
    public string BaseUrl => _configuration[MachineSettingSpecs.ComfyBaseUrl] ?? "";

    /// <summary>Falls back to the historical literal, which is also what the gate node itself falls back to.</summary>
    public string GateToken
    {
        get
        {
            var configured = _configuration["ComfyUI:GateToken"];
            return string.IsNullOrWhiteSpace(configured) ? ComfyOptions.DefaultGateToken : configured.Trim();
        }
    }
}
