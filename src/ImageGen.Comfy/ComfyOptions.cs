namespace ImageGen.Comfy;

/// <summary>
/// Where the renderer is and how to get past its queue guard, read FRESH on every use.
///
/// <para>These two were fields on <see cref="ComfyOptions"/>, captured once at startup, which is what made the
/// renderer's address something you could only change by restarting. They are now a port: the composition root
/// implements it over live configuration, so a change made on the settings page takes effect on the next call.</para>
/// </summary>
public interface IComfyEndpoint
{
    /// <summary>Base URL of the ComfyUI server, e.g. http://localhost:8188. Trailing slash optional.</summary>
    string BaseUrl { get; }

    /// <summary>
    /// Token sent as <c>X-ImageGen-Token</c> so ComfyUI's <c>imagegen_gate</c> custom node accepts the request. It is
    /// a queue guard, not a secret: it exists so the app's fair queue is the only thing that can enqueue or cancel
    /// work on the shared GPU. Must match <c>_IMAGEGEN_KEY</c> in <c>comfy-nodes/imagegen_gate/__init__.py</c>,
    /// which reads <c>IMAGEGEN_GATE_TOKEN</c> and falls back to the same default.
    /// </summary>
    string GateToken { get; }
}

/// <summary>Settings for the ComfyUI adapter that are fixed for the life of the process.</summary>
public sealed class ComfyOptions
{
    /// <summary>The historical gate token, and the fallback the custom node uses when nothing is configured.</summary>
    public const string DefaultGateToken = "ig-queue-only-7Qx2k9Lp4Rf8Zv1";

    /// <summary>
    /// Root of the configuration tree: <c>&lt;path&gt;/workflows/*.json</c> and <c>&lt;path&gt;/models/*.json</c>.
    /// One file per thing, so adding a workflow or a model is dropping a file rather than editing a shipped one.
    /// Empty = disabled. Read once, when the catalog loads — hence not on <see cref="IComfyEndpoint"/>.
    /// </summary>
    public string CatalogPath { get; init; } = "";
}
