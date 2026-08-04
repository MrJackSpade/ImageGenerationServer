using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImageGen.Web.Configuration;

/// <summary>One key as the settings page sees it.</summary>
public sealed record MachineSettingView(
    string Key, string Label, string Help, string Kind, string Store, bool Live, bool Required, string? Value);

/// <summary>
/// Reads and writes this machine's configuration, routing each key to the one place it is kept: the database for
/// everything that can be, the environment's appsettings file for the two keys that open the database.
///
/// <para>Both routes end with the value visible to <see cref="IConfiguration"/> immediately — the database provider
/// reloads and fires its change token, and the JSON provider is watching the file. Whether the running process
/// ACTS on it is a separate question, answered per key by <see cref="SettingApply"/>.</para>
/// </summary>
public sealed class MachineConfigService(
    IConfiguration configuration,
    MachineSettingsConfigurationSource source,
    IWebHostEnvironment environment)
{
    private readonly IConfiguration _configuration = configuration;
    private readonly MachineSettingsConfigurationSource _source = source;
    private readonly IWebHostEnvironment _environment = environment;

    /// <summary>The machine these settings belong to. A shared database holds a set per box.</summary>
    public string MachineName => _source.Provider?.MachineName ?? Environment.MachineName;

    /// <summary>The file the two file-held keys are written to. Never appsettings.json, which is committed.</summary>
    public string OverrideFilePath =>
        Path.Combine(_environment.ContentRootPath, $"appsettings.{_environment.EnvironmentName}.json");

    public IReadOnlyList<MachineSettingView> Describe() =>
        [.. MachineSettingSpecs.All.Select(s => new MachineSettingView(
            s.Key, s.Label, s.Help,
            s.Kind.ToString().ToLowerInvariant(),
            s.Store.ToString().ToLowerInvariant(),
            s.Apply == SettingApply.Live,
            s.Required,
            // The declared default when nothing is stored, so the form shows what the app is actually doing rather
            // than what happens to be in the store. Storing it on render would be worse: a key would stop tracking
            // its default the moment the page was opened.
            _configuration[s.Key] ?? s.Default))];

    /// <summary>True when every required key has a value — i.e. the box has been through first boot.</summary>
    public bool IsConfigured =>
        MachineSettingSpecs.RequiredKeys.All(s => !string.IsNullOrWhiteSpace(_configuration[s.Key]));

    /// <summary>
    /// Store a value. A blank value removes the key rather than storing emptiness, so "unset" and "set to nothing"
    /// stay the same state — which is what the required-key check and first boot read.
    /// </summary>
    public async Task SetAsync(string key, string? value, CancellationToken ct)
    {
        var spec = MachineSettingSpecs.Find(key)
            ?? throw new ArgumentException($"'{key}' is not a machine setting.", nameof(key));

        var stored = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (spec.Store == SettingStore.File) WriteToOverrideFile(spec.Key, stored);
        else
        {
            var provider = _source.Provider
                ?? throw new InvalidOperationException("The machine settings provider has not been built yet.");
            await provider.WriteAsync(spec.Key, stored, ct);
        }
    }

    /// <summary>
    /// Merge one key into the environment's appsettings file, leaving everything else in it alone. A targeted edit
    /// rather than serialising an object over the top: the file stays hand-editable, and a write must not reformat
    /// or discard what it did not change. (JSON comments are not preserved — this file is machine-specific and
    /// gitignored; the documented, commented one is appsettings.json, which is never written here.)
    /// </summary>
    private void WriteToOverrideFile(string key, string? value)
    {
        var path = OverrideFilePath;
        // Absent file: start fresh (the legitimate first-write state). Present file: it MUST already be a JSON object;
        // a valid-but-non-object root (an array, a scalar, the literal null) is not something to silently discard and
        // overwrite — that would lose whatever is in it — so refuse. (Invalid JSON throws in Parse, which is also right.)
        var root = !File.Exists(path)
            ? new JsonObject()
            : JsonNode.Parse(File.ReadAllText(path)) as JsonObject
              ?? throw new InvalidOperationException(
                  $"'{path}' exists but its root is not a JSON object; refusing to overwrite it. Fix or remove the file.");

        var segments = key.Split(':');
        var node = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (node[segments[i]] is not JsonObject child)
            {
                child = new JsonObject();
                node[segments[i]] = child;
            }
            node = child;
        }

        var leaf = segments[^1];
        if (value is null) node.Remove(leaf); else node[leaf] = value;

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
