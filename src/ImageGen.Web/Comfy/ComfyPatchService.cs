using ImageGen.Comfy.Patches;

namespace ImageGen.Web.Comfy;

/// <summary>One patch, as the settings page needs it.</summary>
/// <param name="Occupied">Files this patch would create that are already there holding something else. What "overwrite" would discard.</param>
/// <param name="InstallOnly">
/// This patch installs a pack and changes nothing in it. The page words it differently — "installed" rather than
/// "in place", because presence is all that is verified — and warns that removing it deletes the pack.
/// </param>
public sealed record PatchView(
    string Id,
    string Title,
    string Does,
    string Why,
    string Target,
    string State,
    string? Detail,
    string? Warn,
    bool InstallOnly,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> Occupied);

/// <summary>Everything the patches page renders itself from.</summary>
public sealed record PatchesView(
    string? Root,
    bool RootOk,
    string? RootError,
    bool CanRestart,
    bool Ephemeral,
    bool HasPython,
    IReadOnlyList<PatchView> Patches);

/// <summary>
/// The patch set, bound to this box: which patches exist, where they stand against the configured ComfyUI, and
/// the two things that can be done to one.
///
/// <para>The catalogue is read from disk on every call rather than cached. It is a handful of files, and the
/// alternative is a page that keeps reporting a patch as applied after somebody has edited the tree underneath
/// it — the whole value of deriving state instead of storing it.</para>
/// </summary>
public sealed class ComfyPatchService(
    ComfyInstall install,
    ComfySupervisor supervisor,
    PatchInstaller installer,
    Configuration.MachineConfigService machine,
    IWebHostEnvironment environment,
    ILogger<ComfyPatchService> log)
{
    private static class Payloads
    {
        /// <summary>Payload directory holding the file-tree patches shipped beside the app.</summary>
        public const string PatchesPayload = "comfy-patches";

        /// <summary>Payload directory holding the custom-node packs shipped beside the app.</summary>
        public const string NodesPayload = "comfy-nodes";
    }

    private readonly ComfyInstall _install = install;
    private readonly ComfySupervisor _supervisor = supervisor;
    private readonly PatchInstaller _installer = installer;
    private readonly Configuration.MachineConfigService _machine = machine;
    private readonly IWebHostEnvironment _environment = environment;
    private readonly ILogger<ComfyPatchService> _log = log;

    /// <summary>
    /// The whole page state, adopting a renderer folder if nobody has set one.
    ///
    /// <para>Asking the renderer where it lives and confirming the answer on this filesystem is strictly better
    /// than making somebody type a path they have to look up — and it is what makes "install the pack this
    /// workflow needs" work on a box nobody has configured. It is stored once found, so the detection is not
    /// repeated and the value is visible and editable like any other machine setting.</para>
    /// </summary>
    public async Task<PatchesView> DescribeAsync(CancellationToken ct)
    {
        if (_install.Root is null)
        {
            string? detected = await _install.DetectRootAsync(ct);
            if (detected is not null)
            {
                _log.LogInformation("Renderer folder was unset; the renderer reports {Root}, which is a ComfyUI on this machine", detected);
                await _machine.SetAsync(ComfyInstall.Keys.PathKey, detected, ct);
            }
        }

        ComfyInstallInfo info = _install.Describe();
        List<PatchView> patches = [];

        if (info.Ok)
        {
            string root = info.Root ?? throw new InvalidOperationException("A usable ComfyUI install reported no root path.");
            foreach (ComfyPatch patch in Load())
            {
                (PatchState state, string? detail) = ComfyPatchCatalog.Inspect(patch, root);
                string target = patch.ResolveTarget(root);
                IReadOnlyList<string> occupied = state == PatchState.Conflicted && Directory.Exists(target)
                    ? PatchApplier.Occupied(target, patch.Files)
                    : [];

                patches.Add(new PatchView(patch.Id, patch.Title, patch.Does, patch.Why, patch.Target,
                    state.ToString(), detail, patch.Warn, patch.IsInstallOnly, patch.Provides, occupied));
            }
        }

        return new PatchesView(
            Root: info.Root,
            RootOk: info.Ok,
            RootError: info.Error,
            CanRestart: _supervisor.CanRestart,
            Ephemeral: _supervisor.Ephemeral,
            HasPython: _install.Python is not null,
            Patches: patches);
    }

    /// <summary>Apply one patch. Returns a note when something is left for the operator to do.</summary>
    public async Task<string?> ApplyAsync(string id, bool overwrite, CancellationToken ct)
    {
        string root = _install.RequireRoot();
        ComfyPatch patch = Find(id);
        _log.LogInformation("Applying ComfyUI patch {Id} to {Root} (overwrite: {Overwrite})", id, root, overwrite);
        return await _installer.ApplyAsync(patch, root, _install.Python, overwrite, ct);
    }

    /// <summary>Apply everything not already applied, in order. Stops at the first refusal rather than
    /// carrying on past it: a patch that will not apply is a fact about this installation, not an item to skip.</summary>
    public async Task<IReadOnlyList<string>> ApplyAllAsync(CancellationToken ct)
    {
        string root = _install.RequireRoot();
        List<string> notes = [];

        foreach (ComfyPatch patch in Load())
        {
            if (ComfyPatchCatalog.Inspect(patch, root).State == PatchState.Applied)
            {
                continue;
            }

            string? note = await _installer.ApplyAsync(patch, root, _install.Python, overwrite: false, ct);
            if (note is not null)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    /// <summary>Take one patch back out.</summary>
    public void Remove(string id)
    {
        string root = _install.RequireRoot();
        ComfyPatch patch = Find(id);
        _log.LogInformation("Removing ComfyUI patch {Id} from {Root}", id, root);
        _installer.Remove(patch, root);
    }

    /// <summary>Restart the renderer, where this deployment is the thing that can.</summary>
    public void Restart() => _supervisor.Restart();

    private ComfyPatch Find(string id) =>
        Load().FirstOrDefault(p => p.Id == id)
        ?? throw new InvalidOperationException($"'{id}' is not a patch in this build.");

    private IReadOnlyList<ComfyPatch> Load()
    {
        string? patchDirectory = Payload(Payloads.PatchesPayload);
        string? nodesDirectory = Payload(Payloads.NodesPayload);

        // Neither present is a broken build, not an empty patch set. Rendering "no patches" would read as
        // "nothing to do" on an install that is in fact missing every fix it ships with.
        if (patchDirectory is null && nodesDirectory is null)
        {
            throw new ComfyPatchCatalog.LoadException(
                $"This build carries no patch payload — neither comfy-patches nor comfy-nodes is beside it "
                + $"(looked in {_environment.ContentRootPath} and {AppContext.BaseDirectory}).");
        }

        return ComfyPatchCatalog.Load(patchDirectory, nodesDirectory);
    }

    /// <summary>
    /// Where a payload directory is. The content root is where a container and a release archive put it; the
    /// binary's own directory is where the build copies it, which is what makes this work from a checkout too.
    /// </summary>
    private string? Payload(string name)
    {
        foreach (string? root in new[] { _environment.ContentRootPath, AppContext.BaseDirectory })
        {
            string candidate = Path.Combine(root, name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
