using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

/// <summary>Åpner/lukker Windows' innebygde touch-tastatur (TabTip) via ITipInvocation.</summary>
internal static class TouchKeyboard
{
    [ComImport, Guid("4ce576fa-83dc-4F88-951c-9d0782b4e376")]
    private class UIHostNoLaunch { }

    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation { void Toggle(IntPtr hwnd); }

    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    private static int _busy;

    public static void Toggle()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                PadMouse.Log("TouchKeyboard.Toggle start");
                if (Process.GetProcessesByName("TabTip").Length == 0)
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                        "microsoft shared", "ink", "TabTip.exe");
                    if (File.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        Thread.Sleep(700);
                    }
                    else PadMouse.Log("TabTip.exe finnes ikke: " + path);
                }
                ((ITipInvocation)new UIHostNoLaunch()).Toggle(GetDesktopWindow());
                PadMouse.Log("ITipInvocation.Toggle OK");
            }
            catch (Exception ex)
            {
                // Uten Utforsker (Xbox mode) kan TabTip-COM-objektet mangle. Prøv skjermtastaturet som reserve.
                PadMouse.Log("ITipInvocation feilet: " + ex.GetType().Name + " " + ex.Message + " -> prøver osk.exe");
                try
                {
                    var osk = Process.GetProcessesByName("osk");
                    if (osk.Length > 0) { foreach (var p in osk) p.Kill(); }
                    else Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
                }
                catch (Exception ex2) { PadMouse.Log("osk.exe feilet: " + ex2.Message); }
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }
}
