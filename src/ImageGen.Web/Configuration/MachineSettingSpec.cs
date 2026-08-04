//TODO: CHECK FOR FALLBACKS
namespace ImageGen.Web.Configuration;

/// <summary>Where a key is kept. A key is in exactly one of these — never both.</summary>
public enum SettingStore
{
    /// <summary>A row in <c>dbo.MachineSetting</c>. Everything that can be, is.</summary>
    Database,

    /// <summary>
    /// The environment's appsettings file. Only for what is needed to OPEN the database, which cannot be stored
    /// inside it.
    /// </summary>
    File,
}

/// <summary>Whether a change takes effect where it is made, or waits for the process to restart.</summary>
public enum SettingApply
{
    /// <summary>Read per use, so the next read sees it.</summary>
    Live,

    /// <summary>
    /// Consumed once while the host is being built (a listener bound, a model loaded, the DI graph wired), so the
    /// value is stored immediately but the running process keeps the old one. The UI says so on the key.
    /// </summary>
    Restart,
}

/// <summary>How the UI should render the field.</summary>
public enum SettingKind { Text, Number, Bool }

/// <summary>
/// One machine-configuration key: what it is called, what it means, where it is kept, and when a change bites.
/// The UI renders from this list and the API refuses any key that is not on it — so a settings page cannot be
/// used to write arbitrary configuration into the process.
/// </summary>
/// <param name="Key">The configuration path, exactly as IConfiguration spells it.</param>
/// <param name="Label">Short name for the form. The explanation goes in <paramref name="Help"/>, as a tooltip.</param>
/// <param name="Required">Absent or blank means the box is not configured, and first boot asks for it.</param>
/// <param name="Default">
/// What the key means when nothing has been stored. Declared HERE so the settings page and the code that reads the
/// key cannot disagree: a default written only at the read site is invisible to a page that knows just what is in
/// the store, which is how a check that was running showed as switched off.
/// </param>
public sealed record MachineSettingSpec(
    string Key,
    string Label,
    string Help,
    SettingKind Kind,
    SettingStore Store,
    SettingApply Apply,
    bool Required = false,
    string? Default = null);

/// <summary>
/// The whole configurable surface of the box, in the order the settings page shows it.
///
/// <para>Everything here that says <see cref="SettingStore.Database"/> has been REMOVED from appsettings.json —
/// that is what makes the store the only source. The two <see cref="SettingStore.File"/> keys are the exception
/// that proves it: they are how the database is reached, so they cannot live in it.</para>
/// </summary>
public static class MachineSettingSpecs
{
    public const string ComfyBaseUrl = "ComfyUI:BaseUrl";

    public static readonly IReadOnlyList<MachineSettingSpec> All =
    [
        new(ComfyBaseUrl, "Renderer address",
            "Where ComfyUI is listening, e.g. http://localhost:8188. It must be started with --enable-cors-header.",
            SettingKind.Text, SettingStore.Database, SettingApply.Live, Required: true),

        new("ComfyUI:GateToken", "Renderer queue token",
            "Sent as X-ImageGen-Token so ComfyUI's imagegen_gate node accepts the request. A queue guard, not a "
            + "secret: it exists so this app's fair queue is the only thing that can enqueue work on the shared GPU. "
            + "Must match the node, which reads IMAGEGEN_GATE_TOKEN.",
            SettingKind.Text, SettingStore.Database, SettingApply.Live),

        new("ComfyUI:Path", "Renderer folder",
            "Where ComfyUI is INSTALLED, as opposed to where it is listening. Only the patches page uses it, and "
            + "only this box's own copy can be patched — leave it empty if the renderer is on another machine.",
            SettingKind.Text, SettingStore.Database, SettingApply.Live),

        new("ComfyUI:Python", "Renderer Python",
            "The interpreter that runs that ComfyUI, e.g. its venv's python. Used solely to install the "
            + "requirements of a node pack a patch has just fetched; without it the packages are named and left "
            + "to you, because installing them into the wrong environment fails silently until a node won't import.",
            SettingKind.Text, SettingStore.Database, SettingApply.Live),

        new("Auth:RegistrationCode", "Registration code",
            "New sign-ups must quote this. Blank means anyone who can reach the app can create an account.",
            SettingKind.Text, SettingStore.Database, SettingApply.Live),

        new("Uploads:MinAvailableMemoryMB", "Free-memory floor (MB)",
            "Below this much free physical memory the box refuses new submissions with a 503. Uploaded render "
            + "inputs stay resident until their job runs and are never evicted, so admission control is the only "
            + "point at which saying no costs the caller nothing.",
            SettingKind.Number, SettingStore.Database, SettingApply.Live),

        new("Updates:Enabled", "Check for updates",
            "Asks github.com once per start whether a newer release exists, and shows a banner if so. Turn it "
            + "off to stop this box contacting GitHub at all. A build with no version — anything not from a "
            + "release archive — never checks and never shows the banner.",
            SettingKind.Bool, SettingStore.Database, SettingApply.Restart, Default: "true"),

        new("Civitai:Enabled", "CivitAI lookups",
            "Looks a LoRA up on civitai.com by its file hash — once per file — to fill in its trigger words and a "
            + "preview image on the LoRAs page. Turn it off to stop this box contacting CivitAI at all; trigger "
            + "words then stay whatever you type on the LoRAs page.",
            SettingKind.Bool, SettingStore.Database, SettingApply.Live, Default: "true"),

        new("Diagnostics:ExposeStackTraces", "Expose stack traces",
            "Puts the full exception in the 500 JSON body, which is what makes a failure readable in the UI. Turn "
            + "off where untrusted people can reach the app: the body keeps its shape and carries the message "
            + "instead. The full trace goes to the log file either way.",
            SettingKind.Bool, SettingStore.Database, SettingApply.Live, Default: "true"),

        new("Security:TrustAllProxies", "Trust all proxies",
            "Honour X-Forwarded-* from any caller — correct behind your own reverse proxy. Turn off where the app "
            + "is reachable directly, since these headers are otherwise spoofable.",
            SettingKind.Bool, SettingStore.Database, SettingApply.Restart, Default: "true"),

        new("Reconciler:Enabled", "Run the reconciler",
            "Reaps stale PendingJob rows. Registered as a hosted service at startup.",
            SettingKind.Bool, SettingStore.Database, SettingApply.Restart, Default: "true"),

        new("Logging:FilePath", "Log file",
            "A rolling-by-day log file, resolved against the content root when relative. Blank turns the file sink "
            + "off. Nothing prunes it — removing old files is an operator decision, not a cap this app invents.",
            SettingKind.Text, SettingStore.Database, SettingApply.Restart),

        new("Logging:LogLevel:Default", "Log level",
            "Trace, Debug, Information, Warning, Error or Critical.",
            SettingKind.Text, SettingStore.Database, SettingApply.Restart),

        new("ConnectionStrings:ImageGen", "Database connection string",
            "How this app reaches its database. Kept in the appsettings file because it is what opens the store "
            + "every other setting lives in.",
            SettingKind.Text, SettingStore.File, SettingApply.Restart),

        new("Database:Provider", "Database engine",
            "Sqlite or SqlServer. SQLite needs no server and creates its own schema; SQL Server expects schema.sql "
            + "to be applied out-of-band, because the app's login holds no DDL rights.",
            SettingKind.Text, SettingStore.File, SettingApply.Restart),

        new("Urls", "Listen on",
            "The address Kestrel binds, e.g. http://0.0.0.0:8080. In the file because the listener is bound before "
            + "the app can read anything out of the database.",
            SettingKind.Text, SettingStore.File, SettingApply.Restart),

        new("Kestrel:Limits:MaxRequestBodySize", "Max upload size (bytes)",
            "Editor uploads are the raw file as selected — phone photos and video — so the ~28.6MB default rejects "
            + "them. Keep in step with client_max_body_size in the nginx config. In the file for the same reason as "
            + "the listen address.",
            SettingKind.Number, SettingStore.File, SettingApply.Restart),
    ];

    public static MachineSettingSpec? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The keys first boot has to ask for. Absent or blank means the box is not configured.</summary>
    public static IEnumerable<MachineSettingSpec> RequiredKeys => All.Where(s => s.Required);

    /// <summary>
    /// The value in force for a key nothing has been stored for, as declared on its spec.
    /// </summary>
    public static string? DefaultOf(string key) =>
        (Find(key) ?? throw new ArgumentException($"'{key}' is not a machine setting.", nameof(key))).Default;
}

/// <summary>Reading a machine setting the way its spec says it is meant to be read.</summary>
public static class MachineSettingConfigurationExtensions
{
    /// <summary>
    /// A <see cref="SettingKind.Bool"/> machine setting, falling back to the default its spec declares.
    ///
    /// <para>Use this rather than <c>GetValue(key, true)</c>: the literal at the read site is a second copy of the
    /// default that the settings page cannot see, so the page renders the key off while the app runs it on. Here
    /// there is one copy, and both sides read it.</para>
    /// </summary>
    public static bool IsOn(this IConfiguration config, string key) =>
        bool.Parse(config[key] ?? MachineSettingSpecs.DefaultOf(key)
            ?? throw new InvalidOperationException($"'{key}' declares no default, so an unset box has no answer."));
}
