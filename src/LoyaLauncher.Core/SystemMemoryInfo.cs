using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoyaLauncher.Core;

/// <summary>
/// 物理内存总量查询（跨平台）：
/// Windows 走 GlobalMemoryStatusEx，Linux 读 /proc/meminfo，macOS 走 sysctl。
/// 探测失败返回 null，调用方应跳过相关检查而非误报。
/// </summary>
public static class SystemMemoryInfo
{
    /// <summary>物理内存总字节数；探测失败为 null</summary>
    public static long? TotalBytes { get; } = Probe();

    private static long? Probe()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    // MemTotal:       16304256 kB
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb * 1024;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref status))
                    return unchecked((long)status.ullTotalPhys);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("sysctl", "-n hw.memsize")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    var text = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit();
                    if (long.TryParse(text, out var bytes)) return bytes;
                }
            }
        }
        catch { /* 探测失败：返回 null，调用方跳过检查 */ }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
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

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
