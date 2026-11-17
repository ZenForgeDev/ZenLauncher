using System.Runtime.InteropServices;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>Systemkurv (tray), «lukk til systemkurven», «start skjult» og start med Windows.</summary>
public sealed partial class MainWindow
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private const int SM_SHUTTINGDOWN = 0x2000;

    private TrayIcon? _tray;
    private bool _exiting;

    private void InitTray()
    {
        AppWindow.Closing += (_, e) =>
        {
            var d = _settings.Data;
            // Aldri hindre at Windows slår av eller logger ut.
            if (_exiting || !d.CloseToTray || !DesktopOn || GetSystemMetrics(SM_SHUTTINGDOWN) != 0) return;
            e.Cancel = true;
            HideToTray();
        };
        Closed += (_, _) => { try { _tray?.Dispose(); } catch { } };
        ApplyTrayState();
    }

    /// <summary>Ikonet vises når «lukk til systemkurven» eller «start skjult» er på.</summary>
    private void ApplyTrayState()
    {
        var d = _settings.Data;
        bool want = d.CloseToTray || d.StartInTray;
        if (want && _tray is null)
        {
            _tray = new TrayIcon("ZenLauncher");
            _tray.Open += () => DispatcherQueue.TryEnqueue(ShowFromTray);
            _tray.Exit += () => DispatcherQueue.TryEnqueue(ExitApp);
        }
        else if (!want && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    public bool StartHiddenWanted => _settings.Data.StartInTray;

    public void HideToTray()
    {
        try { AppWindow.Hide(); } catch { }
    }

    private void ShowFromTray()
    {
        try { AppWindow.Show(); } catch { }
        BringToFront();
    }

    private void ExitApp()
    {
        _exiting = true;
        try { _tray?.Dispose(); } catch { }
        Close();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    /// <summary>Lagrer en oppstartsvalg-endring, oppdaterer systemkurven og setter opp/fjerner oppstarten i Windows.</summary>
    private void StartupChanged()
    {
        _settings.Save();
        ApplyTrayState();
        _ = ApplyStartupAsync();
    }

    private async Task ApplyStartupAsync()
    {
        var d = _settings.Data;
        var (ok, msg) = await StartupService.ApplyAsync(d.StartWithWindows, d.FastStartup, d.StartInTray);
        if (!ok) { d.StartWithWindows = false; _settings.Save(); }
        DispatcherQueue.TryEnqueue(() => { if (msg.Length > 0) ShowToast(msg); });
    }

    private void AddStartupEntries(List<MenuEntry> list)
    {
        var d = _settings.Data;
        void Flag(string title, string detail, bool on, Action<bool> set) =>
            list.Add(new(title, (on ? "På – " : "Av – ") + detail)
            {
                KeepOpen = true,
                OnAccept = () => { set(!on); return Task.CompletedTask; },
                OnAdjust = dir => set(dir > 0),
            });
        Flag("Start med Windows", "starter ZenLauncher når du logger inn", d.StartWithWindows, v => { d.StartWithWindows = v; StartupChanged(); });
        Flag("Rask oppstart", "bruker Oppgaveplanlegger, uten forsinkelse", d.FastStartup, v => { d.FastStartup = v; StartupChanged(); });
        Flag("Start skjult i systemkurven", "vinduet vises først når du åpner det", d.StartInTray, v => { d.StartInTray = v; StartupChanged(); });
        Flag("Lukk til systemkurven", "kryss-knappen skjuler appen (Desktop-modus)", d.CloseToTray, v => { d.CloseToTray = v; StartupChanged(); });
    }
}
