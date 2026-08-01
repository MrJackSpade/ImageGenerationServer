using System.Diagnostics;

namespace ImageGen.Comfy.Patches;

/// <summary>
/// Applies and removes a patch against a real ComfyUI directory: fetch the pack if the patch installs one,
/// apply the diff, install what the pack needs to import.
///
/// <para>This sequence lives here, once, because two things run it — the settings page and the image build —
/// and a build that installed patches slightly differently from the UI would produce a container whose state
/// the UI then misreads.</para>
/// </summary>
public sealed class PatchInstaller(PackSource packs, ILogger<PatchInstaller> log)
{
    private readonly PackSource _packs = packs;
    private readonly ILogger<PatchInstaller> _log = log;

    /// <summary>
    /// Put <paramref name="patch"/> in place under <paramref name="comfyRoot"/>.
    /// </summary>
    /// <param name="python">
    /// The interpreter running this ComfyUI, used to install a freshly-fetched pack's requirements. When null
    /// the requirements are NOT installed and the caller is told which file to install by hand — guessing at an
    /// interpreter would install them into the wrong environment, where nothing would report a problem until a
    /// node failed to import.
    /// </param>
    /// <param name="overwrite">
    /// Replace files this patch creates that are already there holding something else. This is how a node pack
    /// whose installed copy has fallen behind the shipped one is brought back into line; it is never inferred,
    /// because what it discards might be somebody's edit.
    /// </param>
    /// <returns>A line to show the operator when something is left for them to do, otherwise null.</returns>
    public async Task<string?> ApplyAsync(ComfyPatch patch, string comfyRoot, string? python, bool overwrite, CancellationToken ct)
    {
        var target = patch.ResolveTarget(comfyRoot);

        if (!Directory.Exists(target))
        {
            if (patch.SourceUrl is null && !patch.CreatesItsTarget)
                throw new PatchConflictException($"{patch.Target} is not installed, and this patch does not say where to get it.");

            if (patch.SourceUrl is not null)
            {
                _log.LogInformation("Fetching {Target} from {Source} at {Rev}", patch.Target, patch.SourceUrl, patch.Rev);
                await _packs.FetchAsync(patch.SourceUrl, patch.Rev!, target, ct);
            }
        }

        PatchApplier.Apply(target, patch.Files, reverse: false, overwrite);
        _log.LogInformation("Applied {Id} to {Target}", patch.Id, target);

        // After applying, not only after fetching. A pack this repo SHIPS can need packages too -- the GGUF loaders
        // need gguf and sentencepiece -- and those are just as absent on a box that has never installed them. pip is
        // cheap and idempotent when everything is already satisfied, so doing it every time costs a second and
        // removes a way for a pack to be present and permanently unable to import.
        return patch.Target == "." ? null : await InstallRequirementsAsync(target, python, ct);
    }

    /// <summary>
    /// Take <paramref name="patch"/> back out — undoing what applying it CONTRIBUTED, which is not the same thing
    /// for every patch.
    ///
    /// <para>A patch that edits somebody else's pack contributed the edit, so removing it reverses the diff and
    /// leaves the pack installed: withdrawing our fix is not a reason to delete their node pack. An INSTALL-ONLY
    /// patch contributed the pack itself and nothing else, so removing it takes the pack — anything less would
    /// make Remove a no-op that reports success.</para>
    /// </summary>
    public void Remove(ComfyPatch patch, string comfyRoot)
    {
        var target = patch.ResolveTarget(comfyRoot);
        if (!Directory.Exists(target)) throw new PatchConflictException($"{patch.Target} is not installed.");

        if (patch.IsInstallOnly)
        {
            Directory.Delete(target, recursive: true);
            _log.LogInformation("Removed {Id}: deleted {Target}", patch.Id, target);
            return;
        }

        PatchApplier.Apply(target, patch.Files, reverse: true);

        // A pack patch owns the directory it created, so once its files are gone the directory goes too. A patch
        // that only edits somebody else's pack does not, which is why this asks the patch rather than guessing
        // from "it looks empty now".
        if (patch.CreatesItsTarget) PatchApplier.RemoveIfSpent(target);

        _log.LogInformation("Removed {Id} from {Target}", patch.Id, target);
    }

    /// <summary>
    /// Install a newly-fetched pack's <c>requirements.txt</c> into the interpreter that runs ComfyUI. Failure to
    /// install is raised, not logged past: a pack whose dependencies are missing does not half-work, it fails to
    /// import and takes its nodes out of the graph with it.
    /// </summary>
    private async Task<string?> InstallRequirementsAsync(string packDirectory, string? python, CancellationToken ct)
    {
        var requirements = Path.Combine(packDirectory, "requirements.txt");
        if (!File.Exists(requirements)) return null;

        if (string.IsNullOrWhiteSpace(python))
            return $"This pack needs the packages in {requirements}. Set the renderer's Python on Settings → This machine "
                 + "to have that done here, or install them into ComfyUI's environment yourself.";

        _log.LogInformation("Installing {Requirements} with {Python}", requirements, python);

        var constraints = PinInstalledTorch(python, ct);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(python,
                ["-m", "pip", "install", "--no-cache-dir", "--constraint", constraints, "-r", requirements])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        // No deadline: pip fetching a large wheel over a slow link is not a failure, and a clock invented here
        // would kill it partway and leave a half-installed environment.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new PatchConflictException(
                $"pip exited {process.ExitCode} installing {requirements}:\n{await stderr}\n{await stdout}");

        return null;
    }

    /// <summary>
    /// A pip constraints file pinning torch to whatever <paramref name="python"/> already has.
    ///
    /// <para>Node packs declare loose requirements like <c>torch&gt;=2.8.0</c>. If pip decides that is unsatisfied
    /// it will fetch torch from PyPI — the DEFAULT build, not the CUDA or ROCm one this environment was assembled
    /// with — and the GPU stack is silently replaced by an install nobody asked for. This pins nothing of its own:
    /// the versions come back out of the environment, so pip may add packages but can never move these.</para>
    /// </summary>
    private string PinInstalledTorch(string python, CancellationToken ct)
    {
        var freeze = new Process
        {
            StartInfo = new ProcessStartInfo(python, ["-m", "pip", "freeze"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        freeze.Start();
        var installed = freeze.StandardOutput.ReadToEnd();
        freeze.WaitForExit();

        if (freeze.ExitCode != 0)
            throw new PatchConflictException($"Could not read the installed packages from {python} — pip freeze exited {freeze.ExitCode}.");

        var pinned = installed
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("torch==", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("torchvision==", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("torchaudio==", StringComparison.OrdinalIgnoreCase));

        var path = Path.Combine(Path.GetTempPath(), $"imagegen-torch-constraints-{Environment.ProcessId}.txt");
        File.WriteAllLines(path, pinned);
        return path;
    }
}
