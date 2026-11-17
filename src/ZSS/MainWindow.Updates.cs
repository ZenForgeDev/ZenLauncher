using Microsoft.UI.Xaml;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>Oppdateringssjekk mot GitHub (siste release). Viser bare beskjed og åpner utgivelsessiden, installerer ikke selv.</summary>
public sealed partial class MainWindow
{
    private string _updateStatus = "";
    private string _updateUrl = "";
    private string _updateLatest = "";
    private bool _updateNewer;
    private bool _updateBusy;
    private string _updateAssetUrl = "", _updateAssetName = "", _updateAssetSha = "";

    /// <summary>Ved oppstart: sjekk stille, og vis bare noe hvis en ny versjon finnes (én gang per versjon).</summary>
    private async Task StartupUpdateCheckAsync()
    {
        var d = _settings.Data;
        if (!d.CheckUpdates || UpdateService.NormalizeRepo(d.UpdateRepo).Length == 0) return;
        await Task.Delay(8000);
        await CheckUpdatesAsync(true);
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        if (!silent) { _updateStatus = "Sjekker…"; RefreshUpdateUi(); }
        try
        {
            var d = _settings.Data;
            var r = await UpdateService.CheckAsync(d.UpdateRepo, AppVersion);
            _updateUrl = r.Url; _updateLatest = r.Latest; _updateNewer = r.Ok && r.Newer;
            _updateAssetUrl = r.AssetUrl; _updateAssetName = r.AssetName; _updateAssetSha = r.AssetSha;
            if (!r.Ok) _updateStatus = silent ? "" : r.Error;
            else if (r.Newer)
            {
                _updateStatus = $"Ny versjon {r.Latest} er ute (du har {AppVersion}).";
                if (d.UpdateSeen != r.Latest)
                {
                    d.UpdateSeen = r.Latest; _settings.Save();
                    ShowToast($"Ny versjon {r.Latest} er ute. Se Innstillinger → Generelt.");
                }
            }
            else _updateStatus = $"Du har siste versjon ({AppVersion}).";
        }
        finally { _updateBusy = false; }
        RefreshUpdateUi();
    }

    private void RefreshUpdateUi()
    {
        if (SettingsPage.Visibility == Visibility.Visible && _settingsPage == 0) BuildSettingsBody();
        else if (!DesktopOn && _mode == Mode.Menu && _menus.Count > 0 && _menus.Peek().Title == "Innstillinger") RebuildMenu(false);
    }

    private void OpenUpdatePage()
    {
        var url = _updateUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? _updateUrl : $"https://github.com/{UpdateService.NormalizeRepo(_settings.Data.UpdateRepo)}/releases";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("Kunne ikke åpne siden: " + ex.Message); }
    }

    /// <summary>Rader for desktop-innstillinger (Generelt).</summary>
    private void BuildUpdateRows(SettingsData d)
    {
        AddToggle("Se etter oppdateringer", "Sjekker GitHub ved oppstart og gir beskjed når en ny versjon er ute. Installerer ingenting selv.", d.CheckUpdates, v => { d.CheckUpdates = v; _settings.Save(); });
        AddTextAction("GitHub-repo", d.UpdateRepo.Length > 0 ? "Nå: " + d.UpdateRepo : "Skriv eier/navn, f.eks. dittnavn/ZenLauncher.", "eier/navn", 100, "Lagre", s =>
        {
            var n = UpdateService.NormalizeRepo(s);
            if (s.Length > 0 && n.Length == 0) { ShowToast("Skriv eier/navn eller hele GitHub-adressen."); return; }
            d.UpdateRepo = n; _settings.Save(); _updateStatus = ""; _updateNewer = false;
            BuildSettingsBody();
        });
        var items = new List<(string, Action)> { ("Sjekk nå", () => _ = CheckUpdatesAsync(false)) };
        if (_updateNewer) items.Add(("Last ned", OpenUpdatePage));
        if (_updateNewer && _updateAssetUrl.Length > 0) items.Add(("Installer nå", () => _ = InstallUpdateAsync()));
        AddRow("Oppdateringer", _updateStatus.Length > 0 ? _updateStatus : "ZenLauncher " + AppVersion, Buttons(items.ToArray()));
    }

    /// <summary>Laster ned setup.exe fra siste release, sjekker sjekksummen (hvis GitHub oppgir den), starter installasjonen og lukker appen.</summary>
    private async Task InstallUpdateAsync()
    {
        if (_updateBusy || _updateAssetUrl.Length == 0) return;
        _updateBusy = true;
        try
        {
            _updateStatus = "Laster ned oppdatering…"; RefreshUpdateUi();
            var (path, err) = await UpdateService.DownloadInstallerAsync(_updateAssetUrl, _updateAssetName, _updateAssetSha, t => DispatcherQueue.TryEnqueue(() => { _updateStatus = t; RefreshUpdateUi(); }));
            if (path is null) { _updateStatus = err; return; }
            _updateStatus = "Starter installasjonen. ZenLauncher lukkes.";
            RefreshUpdateUi();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            await Task.Delay(1500);
            ExitApp();
        }
        catch (Exception ex) { _updateStatus = "Kunne ikke starte installasjonen: " + ex.Message; }
        finally { _updateBusy = false; RefreshUpdateUi(); }
    }
}
