using ImageGen.Application.Platform;
using System.Globalization;

namespace ImageGen.Web.Hosting;

/// <summary>
/// <see cref="ISystemMemory"/> from <c>/proc/meminfo</c>'s <c>MemAvailable</c> — the Linux counterpart of
/// <see cref="WindowsSystemMemory"/>, and what makes the app runnable in a container.
///
/// <para><b>MemAvailable, not MemFree.</b> MemFree counts only genuinely untouched pages and reads near zero on any
/// box that has been up a while, because Linux keeps the page cache full on purpose. A gate reading MemFree would
/// refuse every submission on a perfectly healthy machine. MemAvailable is the kernel's own estimate of what a new
/// allocation could actually obtain, reclaim included, which is exactly the question being asked.</para>
///
/// <para><b>Under a container memory limit this reports the HOST's figure, not the cgroup's.</b> A container capped
/// below the host's available memory will therefore look roomier than it is. Left as is rather than guessed at: the
/// cgroup v1/v2 paths differ, the limit is frequently unset (in which case the cgroup files read as a sentinel
/// meaning "no limit"), and a wrong reading here is worse than a coarse one — this gate decides whether to accept
/// work. Operators who cap the container should set <c>Uploads:MinAvailableMemoryMB</c> accordingly.</para>
/// </summary>
public sealed class LinuxSystemMemory : ISystemMemory
{
    private static class MemInfo
    {
        public const string MemInfoPath = "/proc/meminfo";

        /// <summary>The <c>/proc/meminfo</c> line prefix carrying the kernel's available-memory estimate.</summary>
        public const string MemAvailablePrefix = "MemAvailable:";
    }

    /// <inheritdoc />
    public long AvailableBytes()
    {
        // No fallback on failure, matching the Windows implementation: a gate that cannot read the number must not
        // quietly answer "plenty of room", because that is how an unrenderable job gets accepted.
        string[] lines;
        try
        {
            lines = File.ReadAllLines(MemInfo.MemInfoPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"could not read {MemInfo.MemInfoPath}; available memory is unknown.", ex);
        }

        foreach (string line in lines)
        {
            if (!line.StartsWith(MemInfo.MemAvailablePrefix, StringComparison.Ordinal))
                continue;

            // "MemAvailable:    1234567 kB"
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && long.TryParse(fields[1], CultureInfo.InvariantCulture, out long kilobytes))
                return kilobytes * 1024L;

            throw new InvalidOperationException($"could not parse '{line}' from {MemInfo.MemInfoPath}.");
        }

        throw new InvalidOperationException(
            $"{MemInfo.MemInfoPath} has no MemAvailable line; available memory is unknown. (It has been present since "
            + "Linux 3.14, so this is an unexpected kernel or a substituted procfs.)");
    }
}
