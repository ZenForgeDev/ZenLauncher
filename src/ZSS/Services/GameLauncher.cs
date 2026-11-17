using System.Diagnostics;
using Microsoft.Win32;

namespace ZenStationShell.Services;

public enum LaunchResult { Exited, NeverStarted }

/// <summary>
/// Starter Steam-spill «inne i» ZenStation:
///  - Steam holdes i bakgrunnen (startes med -silent hvis den ikke kjører).
///  - Steams egne vinduer minimeres mens spillet starter, og ZSS-oppstartsskjermen ligger øverst.
///  - Vi følger Steams RunningAppID for å vite når spillet har startet og når det er ferdig.
/// Ærlig grense: Steam må kjøre i bakgrunnen for spill med Steam-DRM/Steamworks. Vi skjuler den, vi fjerner den ikke.
/// </summary>
public sealed class GameLauncher
{
    private static readonly TimeSpan SteamStartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CalmWindow = TimeSpan.FromSeconds(25);      // hvor lenge vi dekker Steam før vi slipper den fram
    private static readonly TimeSpan PostStartGrace = TimeSpan.FromSeconds(10);  // Steam holdes nede litt etter at spillet har startet
    private static readonly TimeSpan NeverStartedTimeout = TimeSpan.FromSeconds(120);

    private const int SW_RESTORE = 9;

    public async Task<LaunchResult> LaunchSteamGameAsync(
        GameEntry game, IntPtr coverHwnd, Action<string> status, Action? onStarted, CancellationToken ct)
    {
        using var suppressCts = new CancellationTokenSource();
        var suppressor = Task.Run(() => SuppressSteamWindowsAsync(suppressCts.Token));

        bool topmost = true;
        NativeMethods.SetTopmost(coverHwnd, true);

        void ReleaseTopmost()
        {
            if (!topmost) return;
            topmost = false;
            NativeMethods.SetTopmost(coverHwnd, false);
        }

        try
        {
            if (!IsSteamReady())
            {
                status("Starter Steam i bakgrunnen…");
                if (!SteamProcessExists())
                {
                    var exe = SteamLibrary.GetSteamExe();
                    if (exe is not null)
                        Process.Start(new ProcessStartInfo(exe, "-silent") { UseShellExecute = false });
                }
                await WaitForSteamAsync(ct);
            }

            status("Starter spillet…");
            Process.Start(new ProcessStartInfo($"steam://rungameid/{game.AppId}") { UseShellExecute = true });

            var sw = Stopwatch.StartNew();
            bool started = false;
            bool released = false;

            while (sw.Elapsed < NeverStartedTimeout)
            {
                ct.ThrowIfCancellationRequested();

                if (GetRunningAppId() == game.AppId)
                {
                    started = true;
                    break;
                }

                if (!released && sw.Elapsed > CalmWindow)
                {
                    // Noe tar tid (oppdatering, skysynk-spørsmål, launcher). Slipp Steam fram så du kan svare.
                    released = true;
                    ReleaseTopmost();
                    suppressCts.Cancel();
                    RestoreSteamWindows();
                    status("Venter på Steam… (svar på eventuelle spørsmål i Steam)");
                }

                await Task.Delay(300, ct);
            }

            if (!started) return LaunchResult.NeverStarted;

            // Spillet er i gang: la det komme foran oss.
            ReleaseTopmost();
            status("");
            onStarted?.Invoke(); // spillet kjører: ZSS kan navigeres mens vi følger det videre

            var grace = Stopwatch.StartNew();
            while (GetRunningAppId() == game.AppId)
            {
                ct.ThrowIfCancellationRequested();
                if (grace.Elapsed > PostStartGrace) suppressCts.Cancel();
                await Task.Delay(1500, ct);
            }

            return LaunchResult.Exited;
        }
        finally
        {
            ReleaseTopmost();
            suppressCts.Cancel();
            try { await suppressor; } catch { /* ignorer */ }
        }
    }

    /// <summary>
    /// Henter fram vinduet til et kjørende Steam-spill (finnes ved at prosessen ligger i spillets mappe).
    /// Returnerer false hvis vi ikke fant et vindu.
    /// </summary>
    public static bool TryActivateGameWindow(GameEntry game)
    {
        if (string.IsNullOrEmpty(game.InstallDir)) return false;
        var marker = $"\\common\\{game.InstallDir}\\";
        var cache = new Dictionary<uint, string>();
        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowTextLength(hwnd) == 0) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!cache.TryGetValue(pid, out var path))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    path = p.MainModule?.FileName ?? "";
                }
                catch
                {
                    path = "";
                }
                cache[pid] = path;
            }

            if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero) return false;
        if (NativeMethods.IsIconic(found)) NativeMethods.ShowWindow(found, SW_RESTORE);
        NativeMethods.SetForegroundWindow(found);
        return true;
    }

    // ── Steam-tilstand ─────────────────────────────────────────────────────

    private static bool SteamProcessExists()
    {
        var procs = Process.GetProcessesByName("steam");
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Steam er klar når klienten har registrert prosess-id i registeret og prosessen lever.</summary>
    private static bool IsSteamReady()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("pid") is int pid && pid != 0)
            {
                using var p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
        }
        catch
        {
            // prosess finnes ikke
        }
        return false;
    }

    private static async Task WaitForSteamAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < SteamStartTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (IsSteamReady())
            {
                await Task.Delay(1500, ct); // gi Steam et øyeblikk til å bli ferdig
                return;
            }
            await Task.Delay(300, ct);
        }
    }

    private static int GetRunningAppId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── Steam-vinduer ──────────────────────────────────────────────────────

    private static async Task SuppressSteamWindowsAsync(CancellationToken ct)
    {
        var cache = new Dictionary<uint, string>();
        while (!ct.IsCancellationRequested)
        {
            ForEachSteamWindow(cache, hwnd =>
            {
                if (NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd))
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
            });

            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void RestoreSteamWindows()
    {
        var cache = new Dictionary<uint, string>();
        ForEachSteamWindow(cache, hwnd =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, SW_RESTORE);
        });
    }

    private static void ForEachSteamWindow(Dictionary<uint, string> nameCache, Action<IntPtr> action)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!nameCache.TryGetValue(pid, out var name))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    name = p.ProcessName.ToLowerInvariant();
                }
                catch
                {
                    name = "";
                }
                nameCache[pid] = name;
            }

            // Kun Steam-klienten og dens nettleserprosess. Overlayet i spillet (gameoverlayui) røres ikke.
            if (name is "steam" or "steamwebhelper")
                action(hwnd);

            return true;
        }, IntPtr.Zero);
    }
}
