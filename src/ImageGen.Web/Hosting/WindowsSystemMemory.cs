//TODO: CHECK FOR FALLBACKS
using System.Runtime.InteropServices;
using ImageGen.Application.Platform;

namespace ImageGen.Web.Hosting;

/// <summary>
/// <see cref="ISystemMemory"/> over the Win32 <c>GlobalMemoryStatusEx</c>, which reports machine-wide available
/// physical memory — the number the submission gate needs. The managed alternatives answer a different question
/// (<c>GC.GetGCMemoryInfo</c> describes this process's heap, not the box), so they cannot stand in here.
/// </summary>
public sealed class WindowsSystemMemory : ISystemMemory
{
    public long AvailableBytes()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        // No fallback on failure. A gate that cannot read the number must not quietly answer "plenty of room" — that
        // is precisely how an unrenderable job gets accepted. Surface the OS error to the caller instead.
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException(
                $"GlobalMemoryStatusEx failed (Win32 error {Marshal.GetLastWin32Error()}); available memory is unknown.");
        return (long)status.ullAvailPhys;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>DllImport rather than the LibraryImport source generator: the generated marshalling stub requires
    /// <c>AllowUnsafeBlocks</c>, and <see cref="MemoryStatusEx"/> is blittable, so the generator would buy nothing for
    /// the cost of turning unsafe code on across the whole web project.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
