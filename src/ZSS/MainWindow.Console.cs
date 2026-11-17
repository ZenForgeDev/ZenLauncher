using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>
/// Konsollmodus (kontroller): innstillinger i kategorier (LB/RB bytter), detaljer, plugins, streaming-klient, nedlastinger og skjermbildevisning.
/// Alt er menyer og enkle visninger som styres med stikker og A/B.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string[] ConsoleCats = { "Generelt", "Visning", "Kontroller", "Strøm og dvale", "System", "Plugins", "Streaming", "Nedlastinger" };
    private int _settingsCat;
    private const int CatStreaming = 6, CatDownloads = 7;

    private static int Cyc(int v, int dir, int n) => ((v + dir) % n + n) % n;

    /// <summary>LB/RB i innstillingsmenyen bytter kategori. Returnerer true når tastetrykket er brukt.</summary>
    private bool MenuTabs(int dir)
    {
        if (DesktopOn || _menus.Count == 0 || _menus.Peek().Title != "Innstillinger") return false;
        _settingsCat = Cyc(_settingsCat, dir, ConsoleCats.Length);
        _sound.Play(Sfx.Move);
        RebuildMenu(true);
        return true;
    }

    private List<MenuEntry> BuildSettingsMenu()
    {
        var d = _settings.Data;
        var list = new List<MenuEntry>
        {
            new($"◄  {ConsoleCats[_settingsCat]}  ►", $"{_settingsCat + 1} av {ConsoleCats.Length}   LB/RB")
            {
                KeepOpen = true,
                OnAccept = () => { _settingsCat = Cyc(_settingsCat, 1, ConsoleCats.Length); return Task.CompletedTask; },
                OnAdjust = dir => _settingsCat = Cyc(_settingsCat, dir, ConsoleCats.Length),
            },
        };

        switch (_settingsCat)
        {
            case 0: // Generelt
                list.Add(new("Desktop-modus", d.DesktopMode ? "På – vanlig vindu, for mus og tastatur" : "Av – fullskjerm som konsoll")
                {
                    KeepOpen = true,
                    OnAccept = () => { d.DesktopMode = !d.DesktopMode; _settings.Save(); ApplyWindowMode(); return Task.CompletedTask; },
                    OnAdjust = dir => { d.DesktopMode = dir > 0; _settings.Save(); ApplyWindowMode(); },
                });
                list.Add(new("Sortering", $"◄ {SortNames[Math.Clamp(d.SortMode, 0, SortNames.Length - 1)]} ►")
                {
                    KeepOpen = true,
                    OnAccept = () => { StepSort(1); return Task.CompletedTask; },
                    OnAdjust = dir => StepSort(dir),
                });
                list.Add(new("Vis Skjult-fanen", d.ShowHiddenTab ? "På – fanen vises og skjulte spill kan hentes fram" : "Av – skjulte spill ses ingen steder")
                {
                    KeepOpen = true,
                    OnAccept = () => { SetShowHiddenTab(!d.ShowHiddenTab); return Task.CompletedTask; },
                    OnAdjust = dir => SetShowHiddenTab(dir > 0),
                });
                list.Add(new("Kontoer…", "Epic-innlogging og bibliotek")
                { OnAccept = () => { OpenMenu("Kontoer", BuildAccountsMenu); return Task.CompletedTask; } });
                list.Add(new("Lyd…", "appvolum, lydutgang, menylyder")
                { OnAccept = () => { OpenMenu("Lyd", BuildSoundMenu); return Task.CompletedTask; } });
                list.Add(new("Bibliotek…", "søk etter spill, legg til app")
                { OnAccept = () => { OpenMenu("Bibliotek", BuildLibraryMenu); return Task.CompletedTask; } });
                list.Add(new("Versjon / oppdateringer", (_updateStatus.Length > 0 ? _updateStatus : "ZenLauncher " + AppVersion) + "  (A: sjekk nå)")
                {
                    KeepOpen = true,
                    OnAccept = async () =>
                    {
                        if (_updateNewer) OpenUpdatePage();
                        else if (UpdateService.NormalizeRepo(d.UpdateRepo).Length == 0) { _updateStatus = "Sett GitHub-repo i desktop-modus (Innstillinger → Generelt)."; RefreshUpdateUi(); }
                        else await CheckUpdatesAsync(false);
                    },
                });
                break;

            case 1: // Visning
                list.Add(new("Parallakse", d.Parallax ? "På – heltebildet flytter seg litt" : "Av")
                {
                    KeepOpen = true,
                    OnAccept = () => { d.Parallax = !d.Parallax; _settings.Save(); ApplyParallax(); return Task.CompletedTask; },
                    OnAdjust = dir => { d.Parallax = dir > 0; _settings.Save(); ApplyParallax(); },
                });
                list.Add(new("Rammefarge", $"◄ {FrameThemes[Math.Clamp(d.FrameTheme, 0, FrameThemes.Length - 1)].Name} ►  (Av og enkeltfarge sparer CPU)")
                {
                    KeepOpen = true,
                    OnAccept = () => { StepFrame(1); return Task.CompletedTask; },
                    OnAdjust = dir => StepFrame(dir),
                });
                list.Add(new("Dimming", $"◄ {MinText(d.DimMinutes)} ►  (skjermen mørkner når du er inaktiv)")
                {
                    KeepOpen = true,
                    OnAccept = () => { StepSetting(1, DimSteps, d.DimMinutes, v => d.DimMinutes = v); return Task.CompletedTask; },
                    OnAdjust = dir => StepSetting(dir, DimSteps, d.DimMinutes, v => d.DimMinutes = v),
                });
                break;

            case 2: // Kontroller
                list.Add(new("Kontroller som mus", d.PadMouse ? "På – i andre apper (Back+Start slår av/på)" : "Av")
                {
                    KeepOpen = true,
                    OnAccept = () => { d.PadMouse = !d.PadMouse; _padMouse.Enabled = d.PadMouse; _settings.Save(); return Task.CompletedTask; },
                    OnAdjust = dir => { d.PadMouse = dir > 0; _padMouse.Enabled = d.PadMouse; _settings.Save(); },
                });
                break;

            case 3: // Strøm og dvale
                list.Add(new("Auto-hvilemodus", $"◄ {MinText(d.SleepMinutes)} ►  (PC-en går i hvilemodus når du er inaktiv)")
                {
                    KeepOpen = true,
                    OnAccept = () => { StepSetting(1, SleepSteps, d.SleepMinutes, v => d.SleepMinutes = v); return Task.CompletedTask; },
                    OnAdjust = dir => StepSetting(dir, SleepSteps, d.SleepMinutes, v => d.SleepMinutes = v),
                });
                list.Add(new("Strøm…", "hvilemodus, start på nytt, slå av, avslutt")
                { OnAccept = () => { OpenMenu("Strøm", BuildPowerMenu); return Task.CompletedTask; } });
                break;

            case 4: // System
                AddStartupEntries(list);
                list.Add(new("Windows og system…", "skjerm, nettverk, Bluetooth, Windows Update")
                { OnAccept = () => { OpenMenu("Windows og system", BuildSystemMenu); return Task.CompletedTask; } });
                break;

            case 5: // Plugins
                list.Add(new("Valgfrie tillegg", "Alle er av som standard. Notater finnes bare i Desktop-modus."));
                AddPluginEntry(list, Plugins.HowLongToBeat.Id, "Tid å fullføre", "henter fra howlongtobeat.com (eksperimentell)");
                AddPluginEntry(list, "sessions", "Spilleøkter", "logger hver gang du spiller");
                AddPluginEntry(list, Plugins.LocalShots.Id, "Egne skjermbilder", "fra Steam-mappen, vises i spillmenyen");
                AddPluginEntry(list, "random", "Tilfeldig spill", "ny rad i hovedmenyen");
                break;

            case CatStreaming:
                AddStreamEntries(list);
                break;

            case CatDownloads:
                list.AddRange(BuildDownloadsMenu());
                StartMenuRefresh();
                break;
        }
        return list;
    }

    private void StepSort(int dir)
    {
        var d = _settings.Data;
        d.SortMode = Cyc(d.SortMode, dir, SortNames.Length);
        _settings.Save();
        RebuildTiles();
    }

    private void AddPluginEntry(List<MenuEntry> list, string id, string title, string detail)
    {
        list.Add(new(title, (PluginOn(id) ? "På – " : "Av – ") + detail)
        {
            KeepOpen = true,
            OnAccept = () => { SetPlugin(id, !PluginOn(id)); return Task.CompletedTask; },
            OnAdjust = dir => SetPlugin(id, dir > 0),
        });
    }

    // ---------- Oppdatering av nedlastingslisten mens den vises ----------

    private Microsoft.UI.Xaml.DispatcherTimer? _menuTimer;
    private string _menuSig = "";

    private void StartMenuRefresh()
    {
        _menuTimer ??= new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _menuTimer.Tick -= MenuTick;
        _menuTimer.Tick += MenuTick;
        _menuTimer.Start();
    }

    private void MenuTick(object? sender, object e)
    {
        if (DesktopOn || _mode != Mode.Menu || _menus.Count == 0 || _menus.Peek().Title != "Innstillinger" || _settingsCat != CatDownloads)
        {
            _menuTimer?.Stop();
            return;
        }
        var sig = DlSignature() + "|" + string.Join("|", _dlQueue.Select(x => x.Detail));
        if (sig == _menuSig) return;
        _menuSig = sig;
        RebuildMenu(false);
    }

    // ---------- Detaljer ----------

    private async Task OpenDetailsAsync(TileVm t)
    {
        ShowToast("Henter detaljer…");
        StoreInfoData? info = null; StoreExtras? extra = null; long size = 0; Plugins.HltbData? hltb = null;
        try
        {
            var dir = t.Game?.InstallPath ?? t.InstallDir;
            var sizeTask = StoreInfo.FolderSizeAsync(dir);
            Task<StoreInfoData?>? infoTask = t.Game is { } g1 ? StoreInfo.GetAsync(g1.AppId) : null;
            Task<StoreExtras>? extraTask = t.Game is { } g2 ? StoreInfo.ExtrasAsync(g2.AppId) : null;
            Task<Plugins.HltbData?>? hltbTask = PluginOn(Plugins.HowLongToBeat.Id) && (t.Game is not null || t.Store is not null)
                ? Plugins.HowLongToBeat.GetAsync(t.Name) : null;
            if (infoTask is not null) info = await infoTask;
            if (extraTask is not null) extra = await extraTask;
            if (hltbTask is not null) hltb = await hltbTask;
            size = await sizeTask;
        }
        catch { /* viser det vi har */ }

        var rows = FactRows(t, info, extra, size, hltb);
        var entries = new List<MenuEntry>();
        if (info is not null)
        {
            if (!string.IsNullOrWhiteSpace(info.Description)) entries.Add(new("Om spillet", info.Description));
            var tags = info.Genres.Take(4).ToList();
            if (info.Metacritic > 0) tags.Add("Metacritic " + info.Metacritic);
            if (tags.Count > 0) entries.Add(new("Sjanger", string.Join(", ", tags)));
        }
        foreach (var (label, value) in rows) entries.Add(new(label, value));
        if (entries.Count == 0) entries.Add(new("Ingen detaljer", "Fant ingenting om dette spillet ennå."));
        OpenMenu("Detaljer: " + t.Name, () => entries);
    }

    // ---------- Egne skjermbilder (fullskjerm) ----------

    private List<string> _shots = new();
    private int _shotIdx;

    private void OpenShotViewer(IEnumerable<string> files)
    {
        _shots = files.ToList();
        if (_shots.Count == 0) return;
        _shotIdx = 0;
        ShowShot();
        ShotViewer.Visibility = Visibility.Visible;
        _mode = Mode.Busy;
    }

    private void ShowShot()
    {
        try { ShotImage.Source = new BitmapImage(new Uri(_shots[_shotIdx])); } catch { ShotImage.Source = null; }
        ShotCaption.Text = $"{_shotIdx + 1} / {_shots.Count}      ◄ ► bla      B lukker";
    }

    private void StepShot(int dir)
    {
        if (_shots.Count == 0) return;
        _shotIdx = Cyc(_shotIdx, dir, _shots.Count);
        _sound.Play(Sfx.Move);
        ShowShot();
    }

    private void CloseShotViewer()
    {
        ShotViewer.Visibility = Visibility.Collapsed;
        ShotImage.Source = null;
        _mode = Mode.Home;
        GameList.Focus(FocusState.Programmatic);
    }

    // ---------- Streaming (klient) ----------

    private static readonly string[] StreamResNames = { "720p", "1080p", "1440p", "4K" };
    private static readonly string[] StreamFpsNames = { "30 bilder/s", "60 bilder/s", "120 bilder/s" };
    private static readonly string[] StreamBitNames = { "10 Mbit/s", "20 Mbit/s", "40 Mbit/s", "80 Mbit/s" };

    private void AddStreamEntries(List<MenuEntry> list)
    {
        var d = _settings.Data;
        if (StreamService.MoonlightExe is null)
        {
            list.Add(new("Installer Moonlight", "lastes ned første gang (ca. 40 MB)")
            { OnAccept = () => { _ = InstallMoonlightUiAsync(); return Task.CompletedTask; } });
            list.Add(new("Strøm fra denne PC-en", "settes opp i Desktop-modus (knappen 📡)"));
            return;
        }

        if (d.LastStreamAddr.Length > 0 && d.LastStreamApp.Length > 0)
        {
            var la = d.LastStreamAddr; var lp = d.LastStreamApp;
            list.Add(new("▶  Fortsett sist", lp + " på " + la)
            { OnAccept = () => { CloseMenus(); PlayStream(la, lp); return Task.CompletedTask; } });
        }

        if (d.StreamHosts.Count == 0) list.Add(new("Ingen verter ennå", "velg «Finn verter i nettverket» under"));
        foreach (var h in d.StreamHosts.ToList())
        {
            var (name, addr) = ParseHost(h);
            list.Add(new(name, addr + "   A: åpne")
            { OnAccept = () => { _ = OpenHostAsync(addr, name); return Task.CompletedTask; } });
        }
        list.Add(new("Finn verter i nettverket", "søker på samme nett")
        { OnAccept = () => { _ = DiscoverUiAsync(); return Task.CompletedTask; } });

        list.Add(new("Oppløsning", $"◄ {StreamResNames[Math.Clamp(d.StreamRes, 0, 3)]} ►")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamRes = Cyc(d.StreamRes, 1, 4); _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamRes = Cyc(d.StreamRes, dir, 4); _settings.Save(); },
        });
        list.Add(new("Bildefrekvens", $"◄ {StreamFpsNames[Math.Clamp(d.StreamFps, 0, 2)]} ►")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamFps = Cyc(d.StreamFps, 1, 3); _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamFps = Cyc(d.StreamFps, dir, 3); _settings.Save(); },
        });
        list.Add(new("Bitrate", $"◄ {StreamBitNames[Math.Clamp(d.StreamBitrate, 0, 3)]} ►")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamBitrate = Cyc(d.StreamBitrate, 1, 4); _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamBitrate = Cyc(d.StreamBitrate, dir, 4); _settings.Save(); },
        });
        list.Add(new("Avslutt spillet når strømmen stopper", d.StreamQuitAfter ? "På" : "Av – spillet fortsetter på verten")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamQuitAfter = !d.StreamQuitAfter; _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamQuitAfter = dir > 0; _settings.Save(); },
        });
        list.Add(new("Begrens kvaliteten over Tailscale", d.StreamAutoQuality ? "På – høyst 1080p og 20 Mbit/s" : "Av")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamAutoQuality = !d.StreamAutoQuality; _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamAutoQuality = dir > 0; _settings.Save(); },
        });
        list.Add(new("Vekk verten automatisk", d.StreamAutoWake ? "På – Wake-on-LAN når verten er av" : "Av")
        {
            KeepOpen = true,
            OnAccept = () => { d.StreamAutoWake = !d.StreamAutoWake; _settings.Save(); return Task.CompletedTask; },
            OnAdjust = dir => { d.StreamAutoWake = dir > 0; _settings.Save(); },
        });
    }

    private async Task OpenHostAsync(string addr, string name)
    {
        if (_streamBusy) return;
        _streamBusy = true;
        ShowToast("Kobler til " + name + "…");
        try
        {
            var (ok, apps, _) = await StreamService.ListAppsAsync(addr);
            if (ok) _streamApps[addr] = apps;
            else { _streamApps.Remove(addr); ShowToast("Ikke paret ennå? Velg Par."); }
        }
        finally { _streamBusy = false; }
        OpenMenu(name, () => BuildHostMenu(addr, name));
    }

    private List<MenuEntry> BuildHostMenu(string addr, string name)
    {
        var list = new List<MenuEntry>();
        if (_streamApps.TryGetValue(addr, out var apps))
        {
            if (apps.Count == 0) list.Add(new("Ingen apper", "verten har ikke lagt til noe å strømme"));
            foreach (var app in apps)
            {
                var a = app;
                list.Add(new("▶  " + a, "A: start strøm")
                { OnAccept = () => { CloseMenus(); PlayStream(addr, a); return Task.CompletedTask; } });
            }
        }
        else list.Add(new("Ikke paret", "velg Par under, og skriv PIN-en på verts-PC-en"));
        list.Add(new("Vekk", "sender Wake-on-LAN (samme nett)")
        { OnAccept = () => { _ = WakeUiAsync(addr); return Task.CompletedTask; } });
        list.Add(new("Par", "viser en PIN som skrives inn på verten")
        { OnAccept = () => { _ = PairConsoleAsync(addr, name); return Task.CompletedTask; } });
        list.Add(new("Fjern vert", name)
        {
            OnAccept = () =>
            {
                _settings.Data.StreamHosts.RemoveAll(x => ParseHost(x).Addr == addr);
                _streamApps.Remove(addr);
                _settings.Save();
                Back();
                return Task.CompletedTask;
            },
        });
        return list;
    }

    private async Task PairConsoleAsync(string addr, string name)
    {
        if (StreamService.MoonlightExe is null) { ShowToast("Installer Moonlight først."); return; }
        if (_streamBusy) return;
        _streamBusy = true;
        var pin = Random.Shared.Next(0, 10000).ToString("D4");
        CloseMenus();
        _installing = true; // hindrer at banneret skjules av nedlastingsvakten
        InstallTitle.Text = "Parer med " + name;
        InstallBar.IsIndeterminate = true;
        InstallDetail.Text = $"PIN {pin}: skriv den inn på verts-PC-en (ZenLauncher → Streaming → Vert → Par)";
        InstallBanner.Visibility = Visibility.Visible;
        try
        {
            var (ok, msg) = await StreamService.PairAsync(addr, pin);
            ShowToast(ok ? "Paret med " + name : msg);
        }
        finally
        {
            _installing = false;
            InstallBanner.Visibility = Visibility.Collapsed;
            _streamBusy = false;
        }
    }
}
