using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ImageGen.Web.Comfy;

/// <summary>
/// Restarts ComfyUI, where something is supervising it.
///
/// <para>ComfyUI reads its custom nodes and imports every module ONCE, at startup. Applying a patch therefore
/// changes nothing that is running: the process keeps executing the code it loaded. Somebody has to restart it,
/// and in the container the app is the only thing that can — so it does, rather than telling the person looking
/// at the page to go and find a terminal.</para>
///
/// <para>Outside the container it deliberately cannot. A release build has no idea how ComfyUI was started —
/// a scheduled task, a service, a shell — and killing a process it did not launch, hoping something restarts it,
/// is how the renderer ends up simply gone. There, the page says a restart is needed and leaves it at that.</para>
///
/// <para>The mechanism is a directory the entrypoint owns: it writes <c>comfy.pid</c> each time it starts
/// ComfyUI, and treats an exit as deliberate only when <c>comfy-restarting</c> is present. So a requested
/// restart and a crash stay distinguishable, and a crash still takes the container down, which is the honest
/// signal it has always been.</para>
/// </summary>
public sealed class ComfySupervisor(IConfiguration config, ILogger<ComfySupervisor> log)
{
    /// <summary>
    /// The directory the container entrypoint shares with this process. Set by the image, never by a user: it
    /// describes how this deployment is run, which is not something to configure from inside it.
    /// </summary>
    public const string DirectoryKey = Configuration.MachineSettingSpecs.ComfySupervisor;

    private const string PidFile = "comfy.pid";
    private const string RestartMarker = "comfy-restarting";
    private const int Sigterm = 15;

    private readonly IConfiguration _config = config;
    private readonly ILogger<ComfySupervisor> _log = log;

    private string? Directory
    {
        get
        {
            string? dir = _config[DirectoryKey];
            return string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
        }
    }

    /// <summary>
    /// True when this deployment supervises ComfyUI AND it is running right now. Both halves matter: the first
    /// is why a release build shows a note instead of a button, the second is why the button does not appear
    /// while the backend is already down.
    /// </summary>
    public bool CanRestart => ReadPid() is not null;

    /// <summary>
    /// True when ComfyUI is part of this container image rather than an installation of the operator's — which
    /// is what makes patch changes here last only until the container is recreated.
    /// </summary>
    public bool Ephemeral => Directory is not null;

    /// <summary>
    /// Ask the supervisor to restart ComfyUI: mark the exit as deliberate, then signal it to stop. The
    /// entrypoint sees the marker and starts it again instead of taking the container down.
    /// </summary>
    public void Restart()
    {
        string directory = Directory ?? throw new InvalidOperationException(
            "This installation does not manage ComfyUI, so it cannot restart it. Restart it yourself for the patches to take effect.");

        int pid = ReadPid() ?? throw new InvalidOperationException(
            "ComfyUI does not appear to be running — there is no process to restart.");

        string marker = Path.Combine(directory, RestartMarker);
        File.WriteAllText(marker, pid.ToString());

        if (!OperatingSystem.IsLinux())
        {
            File.Delete(marker);
            throw new PlatformNotSupportedException("The supervisor protocol is the container entrypoint's, which is Linux.");
        }

        if (Kill(pid, Sigterm) != 0)
        {
            // The marker must not outlive a failed signal: left behind, the NEXT genuine crash would be read as a
            // requested restart and the container would quietly come back instead of reporting that it fell over.
            File.Delete(marker);
            int error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"Could not signal ComfyUI (pid {pid}): errno {error}.");
        }

        _log.LogInformation("Asked the supervisor to restart ComfyUI (pid {Pid})", pid);
    }

    /// <summary>ComfyUI's process id, if the supervisor has written one and that process is still alive.</summary>
    private int? ReadPid()
    {
        string? directory = Directory;
        if (directory is null) return null;

        string path = Path.Combine(directory, PidFile);
        if (!File.Exists(path)) return null;
        if (!int.TryParse(File.ReadAllText(path).Trim(), out int pid) || pid <= 1) return null;

        // A pid file outlives the process it names. Ask whether it is still there rather than believing the file.
        try
        {
            using Process _ = System.Diagnostics.Process.GetProcessById(pid);
            return pid;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>DllImport rather than the LibraryImport source generator, for the same reason
    /// <see cref="Hosting.WindowsSystemMemory"/> uses one: the generated stub requires <c>AllowUnsafeBlocks</c>,
    /// and two ints need no marshalling, so the generator would buy nothing for the cost of unsafe code.</summary>
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}
