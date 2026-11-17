using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

internal static class NativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public const int SW_SHOWMINNOACTIVE = 7;

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>Sekunder siden siste mus- eller tastaturinput i Windows.</summary>
    public static double SystemIdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return double.MaxValue;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
    }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Full sti til exe-filen til en prosess. Virker også for prosesser som kjører som administrator. Null hvis ukjent.</summary>
    public static string? GetProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>True hvis en kjørende prosess har exe-filen sin under mappen (prefix må ende med bakstrek).</summary>
    public static bool AnyProcessUnder(string dirPrefix)
    {
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4) continue;
                var path = GetProcessPath(p.Id);
                if (path is not null && path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    public static void SetTopmost(IntPtr hWnd, bool topmost) =>
        SetWindowPos(hWnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
