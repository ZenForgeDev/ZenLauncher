using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>Streaming-siden: Av / Klient (Moonlight) / Vert (Apollo). Alt styres herfra, ingen andre programvinduer trengs for oppsett.</summary>
public sealed partial class MainWindow
{
    private const int PageStreaming = 8;
    private string _streamMsg = "";
    private TextBlock? _streamMsgTb;
    private string _apolloState = "";
    private bool _driverKnown, _driverOk;
    private List<string> _pairedClients = new();
    private readonly Dictionary<string, List<string>> _streamApps = new();
    private System.Diagnostics.Process? _streamProc;
    private bool _streamBusy;
    private bool _streamRefreshing;
    private string _streamErr = "";
    private DateTime _streamTilesAt = DateTime.MinValue;

    /// <summary>Strømmeflisene i Streaming-fanen: én per app per vert, hentet fra Moonlight (må være paret).</summary>
    private List<TileVm> StreamTiles()
    {
        var list = new List<TileVm>();
        foreach (var h in _settings.Data.StreamHosts)
        {
            var (name, addr) = ParseHost(h);
            if (!_streamApps.TryGetValue(addr, out var apps)) continue;
            foreach (var app in apps)
                list.Add(new TileVm
                {
                    Kind = TileKind.App, Key = $"stream:{addr}|{app}", Name = app, StoreName = name,
                    Glyph = app.Length > 0 ? app[..1].ToUpperInvariant() : "?",
                    App = new AppEntry(app, "stream://" + addr),
                });
        }
        return list;
    }

    /// <summary>Henter appene fra alle vertene (maks hvert 2. minutt, med mindre force). Bygger fanen på nytt når listen endret seg.</summary>
    private void RefreshStreamTiles(bool force)
    {
        if (_streamRefreshing || StreamService.MoonlightExe is null || _settings.Data.StreamHosts.Count == 0) return;
        if (!force && (DateTime.UtcNow - _streamTilesAt).TotalSeconds < 120) return;
        _streamRefreshing = true;
        _streamErr = "";
        _ = Task.Run(async () =>
        {
            bool changed = false;
            var hosts = _settings.Data.StreamHosts.ToList();
            var jobs = hosts.Select(async h =>
            {
                var (name, addr) = ParseHost(h);
                try
                {
                    var (ok, apps, msg) = await StreamService.ListAppsAsync(addr);
                    if (!ok) { if (_streamErr.Length == 0) _streamErr = $"{name}: {(msg.Length > 0 ? msg : "ingen svar")}"; return; }
                    if (!_streamApps.TryGetValue(addr, out var old) || !old.SequenceEqual(apps)) changed = true;
                    _streamApps[addr] = apps;
                    if (apps.Count == 0 && _streamErr.Length == 0) _streamErr = $"{name}: verten har ingen apper";
                }
                catch (Exception ex) { if (_streamErr.Length == 0) _streamErr = $"{name}: {ex.Message}"; }
            }).ToList();
            await Task.WhenAll(jobs);
            DispatcherQueue.TryEnqueue(() =>
            {
                _streamRefreshing = false;
                _streamTilesAt = DateTime.UtcNow;
                if (_filter == FStream && _search.Length == 0 && _mode == Mode.Home && (changed || _tiles.Count == 0)) RebuildTiles();
            });
        });
    }

    private void PlayStreamTile(TileVm t)
    {
        var key = t.Key["stream:".Length..];
        int i = key.IndexOf('|');
        if (i < 0) return;
        _sound.Play(Sfx.Select);
        PlayStream(key[..i], key[(i + 1)..]);
    }

    private void DesktopStream_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        ShowSettingsPage(PageStreaming);
    }

    private bool _dlToasted;

    private void SetStreamMsg(string t)
    {
        _streamMsg = t;
        if (!DesktopOn && t.Length > 0)
        {
            bool progress = t.StartsWith("Laster ned", StringComparison.Ordinal);
            if (!progress || !_dlToasted) ShowToast(t);
            _dlToasted = progress;
        }
        if (_streamMsgTb is not null)
        {
            _streamMsgTb.Text = t;
            _streamMsgTb.Visibility = t.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RebuildStreamPage()
    {
        if (SettingsPage.Visibility == Visibility.Visible && _settingsPage == PageStreaming) BuildSettingsBody();
        else if (!DesktopOn && _mode == Mode.Menu && _menus.Count > 0 && _menus.Peek().Title == "Innstillinger") RebuildMenu(false);
    }

    private void AddTextAction(string title, string? desc, string placeholder, int maxLen, string button, Action<string> act)
    {
        var tb = new TextBox { PlaceholderText = placeholder, MinWidth = 170, MaxLength = maxLen };
        var b = new Button { Content = button, CornerRadius = new CornerRadius(8) };
        b.Click += (_, _) => act(tb.Text.Trim());
        tb.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) act(tb.Text.Trim()); };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(tb);
        sp.Children.Add(b);
        AddRow(title, desc, sp);
    }

    private StackPanel Buttons(params (string Label, Action Click)[] items)
    {
        // Maks 2 knapper per linje, så raden aldri blir bredere enn innstillingsvinduet.
        var col = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        StackPanel? row = null;
        int n = 0;
        foreach (var (label, click) in items)
        {
            if (n % 2 == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
                col.Children.Add(row);
            }
            var b = new Button { Content = label, CornerRadius = new CornerRadius(8) };
            var c = click;
            b.Click += (_, _) => c();
            row!.Children.Add(b);
            n++;
        }
        return col;
    }

    private void BuildStreamingPage()
    {
        var d = _settings.Data;
        AddCombo("Modus", "Klient: spill fra en annen PC. Vert: la andre spille fra denne PC-en.",
            new[] { "Av", "Klient (spill fra en annen PC)", "Vert (strøm denne PC-en)" }, Math.Clamp(d.StreamMode, 0, 2), i =>
            {
                d.StreamMode = i;
                _settings.Save();
                DispatcherQueue.TryEnqueue(() => BuildSettingsBody());
            });

        _streamMsgTb = new TextBlock
        {
            Text = _streamMsg, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Br(0xFF, 0x9F, 0xD0, 0xFF),
            Margin = new Thickness(0, 8, 0, 8), Visibility = _streamMsg.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        SettingsBody.Children.Add(_streamMsgTb);

        if (d.StreamMode == 1) BuildStreamClient(d);
        else if (d.StreamMode == 2) BuildStreamHost(d);
        else AddNote("Streaming er av. Velg Klient eller Vert ovenfor. Komponentene lastes ned først når du trenger dem.");

        AddNote("Loggfil ved feil: " + StreamService.LogFile);
    }

    // ---------- Klient ----------

    private static (string Name, string Addr) ParseHost(string s)
    {
        var i = s.IndexOf('|');
        return i < 0 ? (s, s) : (s[..i], s[(i + 1)..]);
    }

    private void BuildStreamClient(SettingsData d)
    {
        var exe = StreamService.MoonlightExe;
        AddButton("Moonlight (klient)",
            exe is null ? "Ikke installert. Lastes ned fra moonlight-stream (GPL-3.0), ca. 40 MB." : "Installert. Selve strømmen vises i Moonlights eget vindu.",
            exe is null ? "Installer" : "Oppdater", () => _ = InstallMoonlightUiAsync());

        AddCombo("Oppløsning", null, new[] { "720p", "1080p", "1440p", "4K" }, d.StreamRes, i => { d.StreamRes = i; _settings.Save(); });
        AddCombo("Bildefrekvens", null, new[] { "30", "60", "120" }, d.StreamFps, i => { d.StreamFps = i; _settings.Save(); });
        AddCombo("Bitrate", "Høyere gir bedre bilde, men krever bedre nett.", new[] { "10 Mbit/s", "20 Mbit/s", "40 Mbit/s", "80 Mbit/s" }, d.StreamBitrate,
            i => { d.StreamBitrate = i; _settings.Save(); });
        AddToggle("Avslutt spillet på verten når strømmen stopper", "Av: spillet fortsetter å kjøre på verten, og du kan hoppe inn igjen.", d.StreamQuitAfter,
            v => { d.StreamQuitAfter = v; _settings.Save(); });
        AddToggle("Begrens kvaliteten over Tailscale", "Når verten nås via Tailscale (100.x.x.x), brukes høyst 1080p og 20 Mbit/s. Av gir full kvalitet også over VPN.", d.StreamAutoQuality,
            v => { d.StreamAutoQuality = v; _settings.Save(); });
        AddToggle("Vekk verten automatisk (Wake-on-LAN)", "Er verten av, vekkes den når du starter en strøm. Krever MAC-adressen (se under vertene), Wake-on-LAN på i BIOS og at dere står på samme lokalnett.", d.StreamAutoWake,
            v => { d.StreamAutoWake = v; _settings.Save(); });
        if (d.LastStreamAddr.Length > 0 && d.LastStreamApp.Length > 0)
            AddButton("Fortsett sist", $"{d.LastStreamApp} på {d.LastStreamAddr}", "Spill", () => PlayStream(d.LastStreamAddr, d.LastStreamApp));

        SettingsBody.Children.Add(new TextBlock { Text = "Verter", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Br(255, 255, 255, 255), Margin = new Thickness(0, 18, 0, 4) });

        if (d.StreamHosts.Count == 0) AddNote("Ingen verter ennå. Søk i nettverket eller legg til en adresse.");
        foreach (var h in d.StreamHosts.ToList())
        {
            var (name, addr) = ParseHost(h);
            var hh = h; var aa = addr;
            AddRow(name, aa == name ? null : aa, Buttons(
                ("Par", () => _ = PairUiAsync(aa)),
                ("Apper", () => _ = AppsUiAsync(aa)),
                ("Vekk", () => _ = WakeUiAsync(aa)),
                ("Kopier adresse", () => CopyText(aa, "Adresse kopiert.")),
                ("Fjern", () => { d.StreamHosts.Remove(hh); _streamApps.Remove(aa); _settings.Save(); BuildSettingsBody(); })));
            d.WolMacs.TryGetValue(aa, out var knownMac);
            AddTextAction("MAC-adresse for " + name, string.IsNullOrEmpty(knownMac) ? "For Wake-on-LAN. «Vekk» prøver å finne den selv på lokalnettet. Over Tailscale må du skrive den inn selv." : "Lagret: " + knownMac,
                "AA:BB:CC:DD:EE:FF", 17, "Lagre", m =>
                {
                    var norm = WolService.NormalizeMac(m);
                    if (m.Length > 0 && norm.Length == 0) { SetStreamMsg("Ugyldig MAC-adresse. Skriv for eksempel AA:BB:CC:DD:EE:FF."); return; }
                    if (norm.Length == 0) d.WolMacs.Remove(aa); else d.WolMacs[aa] = norm;
                    _settings.Save();
                    BuildSettingsBody();
                });
            if (_streamApps.TryGetValue(aa, out var apps))
            {
                if (apps.Count == 0) AddNote("Verten har ingen apper.");
                foreach (var app in apps)
                {
                    var ap = app;
                    AddButton("      ▶  " + ap, null, "Spill", () => PlayStream(aa, ap));
                }
            }
        }

        AddButton("Finn verter i nettverket", "Søker etter Apollo/Sunshine på samme nett (mDNS). Virker ikke over VPN: legg da til adressen under (f.eks. Tailscale-IP).",
            "Søk", () => _ = DiscoverUiAsync());
        AddTextAction("Legg til vert", "IP-adresse eller navn.", "192.168.1.20", 0, "Legg til", s =>
        {
            if (s.Length == 0) return;
            if (!d.StreamHosts.Any(x => ParseHost(x).Addr == s)) { d.StreamHosts.Add(s + "|" + s); _settings.Save(); }
            BuildSettingsBody();
        });
    }

    private async Task InstallMoonlightUiAsync()
    {
        if (_streamBusy) return;
        _streamBusy = true;
        try
        {
            var (_, msg) = await StreamService.InstallMoonlightAsync(SetStreamMsg);
            SetStreamMsg(msg);
        }
        finally { _streamBusy = false; }
        RebuildStreamPage();
    }

    private async Task DiscoverUiAsync()
    {
        if (_streamBusy) return;
        _streamBusy = true;
        SetStreamMsg("Søker…");
        try
        {
            var hosts = await StreamService.DiscoverAsync();
            var d = _settings.Data;
            int added = 0;
            foreach (var h in hosts)
                if (!d.StreamHosts.Any(x => ParseHost(x).Addr == h.Address)) { d.StreamHosts.Add(h.Name + "|" + h.Address); added++; }
            _settings.Save();
            SetStreamMsg(hosts.Count == 0 ? "Fant ingen verter. Legg til adressen manuelt." : $"Fant {hosts.Count} vert(er), {added} nye.");
        }
        finally { _streamBusy = false; }
        RebuildStreamPage();
    }

    private async Task WakeUiAsync(string addr)
    {
        var d = _settings.Data;
        d.WolMacs.TryGetValue(addr, out var mac);
        if (string.IsNullOrEmpty(mac))
        {
            SetStreamMsg("Leter etter MAC-adressen på lokalnettet…");
            mac = await WolService.ResolveMacAsync(addr);
            if (string.IsNullOrEmpty(mac)) { SetStreamMsg("Fant ikke MAC-adressen. Skriv den inn under verten (finnes på verts-PC-en med ipconfig /all, «Fysisk adresse»)."); return; }
            d.WolMacs[addr] = mac; _settings.Save();
        }
        int n = WolService.Send(mac);
        SetStreamMsg(n > 0 ? $"Vekkesignal sendt til {mac}. Gi PC-en et halvt minutt." : "Fant ingen nettverkskort å sende fra.");
        RebuildStreamPage();
    }

    private async Task PairUiAsync(string addr)
    {
        if (StreamService.MoonlightExe is null) { SetStreamMsg("Installer Moonlight først."); return; }
        if (_streamBusy) return;
        _streamBusy = true;
        var pin = Random.Shared.Next(0, 10000).ToString("D4");
        SetStreamMsg($"PIN: {pin}. Skriv den inn på verts-PC-en (ZenLauncher → Streaming → Vert → Par, eller Apollos nettside). Venter opptil 2 minutter…");
        try
        {
            var (ok, msg) = await StreamService.PairAsync(addr, pin);
            SetStreamMsg(ok ? "Paret med " + addr + ". Trykk Apper for å se hva du kan strømme." : msg);
        }
        finally { _streamBusy = false; }
    }

    private async Task AppsUiAsync(string addr)
    {
        if (_streamBusy) return;
        _streamBusy = true;
        SetStreamMsg("Henter apper…");
        try
        {
            var (ok, apps, msg) = await StreamService.ListAppsAsync(addr);
            if (ok) { _streamApps[addr] = apps; SetStreamMsg(""); }
            else SetStreamMsg("Kunne ikke hente apper. Er dere paret? " + msg);
        }
        finally { _streamBusy = false; }
        RebuildStreamPage();
    }

    private bool _streamWaking;

    /// <summary>Starter en strøm. Er verten av og MAC-adressen kjent, vekkes den først (Wake-on-LAN, opptil 90 sekunder).</summary>
    private void PlayStream(string addr, string app) => _ = PlayStreamAsync(addr, app);

    private async Task PlayStreamAsync(string addr, string app)
    {
        if (_streamProc is { HasExited: false }) { SetStreamMsg("En strøm kjører allerede."); return; }
        var d = _settings.Data;
        if (d.StreamAutoWake && !_streamWaking && d.WolMacs.TryGetValue(addr, out var mac) && mac.Length > 0
            && await StreamService.ProbeHostAsync(addr) is null)
        {
            _streamWaking = true;
            try
            {
                SetStreamMsg("Verten svarer ikke. Vekker den (Wake-on-LAN)…");
                bool up = await WolService.WakeAndWaitAsync(addr, mac, 90);
                if (!up) { SetStreamMsg("Verten våknet ikke innen 90 sekunder. Sjekk at Wake-on-LAN er på i BIOS og at PC-en står på samme nett."); return; }
                SetStreamMsg("Verten er våken. Starter strømmen…");
                await Task.Delay(6000); // gi Apollo tid til å starte
            }
            finally { _streamWaking = false; }
        }
        PlayStreamNow(addr, app);
    }

    private void PlayStreamNow(string addr, string app)
    {
        if (_streamProc is { HasExited: false }) { SetStreamMsg("En strøm kjører allerede."); return; }
        var d = _settings.Data;
        d.LastStreamAddr = addr; d.LastStreamApp = app; _settings.Save();
        int res = d.StreamRes, fps = d.StreamFps, br = d.StreamBitrate;
        if (d.StreamAutoQuality && WolService.IsTailscale(addr)) { res = Math.Min(res, 1); br = Math.Min(br, 1); }
        _streamProc = StreamService.StartStream(addr, app, res, fps, br, d.StreamQuitAfter);
        if (_streamProc is null) { SetStreamMsg("Kunne ikke starte Moonlight."); return; }
        SetStreamMsg($"Strømmer «{app}». Avslutt med Ctrl+Alt+Shift+Q i Moonlight.");
        _streamProc.EnableRaisingEvents = true;
        _padMouse.Suspended = true; // kontrolleren styrer verten nå, ikke musepekeren her
        _streamProc.Exited += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _padMouse.Suspended = false;
            SetStreamMsg("Strømmen er avsluttet.");
            try { Activate(); } catch { }
        });
    }

    private void CopyText(string text, string msg)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            SetStreamMsg(msg);
        }
        catch (Exception ex) { SetStreamMsg("Kunne ikke kopiere: " + ex.Message); }
    }

    // ---------- Vert ----------

    private void BuildStreamHost(SettingsData d)
    {
        bool inst = StreamService.ApolloDir is not null;
        bool hasCreds = d.ApolloUser.Length > 0;
        if (inst && hasCreds && _apolloState == "") { _apolloState = "Sjekker…"; _ = RefreshApolloAsync(); }

        string desc = !inst
            ? "Ikke installert. Lastes ned fra ClassicOldSong/Apollo (GPL-3.0). Windows spør om tillatelse én gang."
            : !hasCreds ? "Installert, men ikke satt opp av ZenLauncher ennå. «Sett opp» lager en innlogging og starter Apollo på nytt."
            : "Status: " + _apolloState;
        var items = new List<(string, Action)>();
        if (!inst) items.Add(("Installer", () => _ = InstallApolloUiAsync()));
        else if (!hasCreds) items.Add(("Sett opp", () => _ = InstallApolloUiAsync()));
        else
        {
            items.Add(("Sjekk på nytt", () => { _apolloState = ""; _driverKnown = false; _apolloSynced = false; BuildSettingsBody(); }));
            items.Add(("Start automatisk", () => _ = ApolloAutostartAsync(true)));
            if (_apolloState.StartsWith("Innlogging", StringComparison.Ordinal)) items.Add(("Sett opp", () => _ = InstallApolloUiAsync()));
            items.Add(("Åpne Apollo", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://localhost:47990") { UseShellExecute = true })));
            items.Add(("Kopier brukernavn", () => CopyText(d.ApolloUser, "Brukernavn kopiert.")));
            items.Add(("Kopier passord", () => CopyText(d.ApolloPass, "Passord kopiert.")));
        }
        AddRow("Apollo (vert)", desc, Buttons(items.ToArray()));

        if (!inst || !hasCreds)
        {
            AddNote("Apollo er en gaffel av Sunshine med innebygd virtuell skjerm (SudoVDA). Installasjonen kjører stille; hvis virtuell skjerm mangler etterpå, kjør Apollo-installasjonen manuelt og velg driveren.");
            return;
        }

        if (_apolloState != "Kjører") return;
        if (!_driverKnown) { _driverKnown = true; _ = RefreshDriverAsync(); }
        AddNote(_driverOk ? "Driver for virtuell skjerm: funnet." : "Driver for virtuell skjerm: ikke funnet (sjekken er ikke garantert pålitelig). Uten den kan ikke Apollo lage virtuell skjerm.");

        AddToggle("Virtuell skjerm", "Strømmen får sin egen skjerm i riktig oppløsning, og bildet på PC-en din røres ikke.", d.StreamVirtual,
            v => _ = ApplyVirtualAsync(v));
        AddToggle("Slå av skjermene på denne PC-en mens noen strømmer", "Skjermene slås på igjen når strømmen stopper. Krever virtuell skjerm.", d.StreamHostScreensOff,
            v => _ = ApplyScreensAsync(v));

        AddTextAction("Par ny klient", "Start paringen på klienten først. Skriv så inn PIN-en den viser.", "PIN (4 siffer)", 4, "Par", s => _ = SubmitPinUiAsync(s));
        if (_pairedClients.Count > 0) AddNote("Parede klienter: " + string.Join(", ", _pairedClients));
        AddButton("Start Apollo på nytt", "Bruk hvis noe henger.", "Start på nytt", () => _ = RestartApolloUiAsync());
        AddButton("Fiks skjermer", "Samme som Ctrl+Win+Shift+B. Bruk hvis skjermene står feil etter en strøm. Skjer også automatisk når strømmen er slutt.", "Fiks nå", () => StreamService.ResetGraphicsStack());
    }

    private DispatcherQueueTimer? _hostWatch;
    private bool _hostWasStreaming;

    /// <summary>Følger med på om noen strømmer fra denne PC-en. Når strømmen er slutt, tilbakestilles grafikkdriveren (som Ctrl+Win+Shift+B), slik at skjermene og oppløsningen blir normale igjen.</summary>
    private void StartHostWatch()
    {
        if (_hostWatch is not null) return;
        _hostWatch = DispatcherQueue.CreateTimer();
        _hostWatch.Interval = TimeSpan.FromSeconds(5);
        _hostWatch.Tick += async (_, _) =>
        {
            var d = _settings.Data;
            if (d.ApolloUser.Length == 0 || StreamService.ApolloDir is null) return;
            var c = await StreamService.AnyClientConnectedAsync(d.ApolloUser, d.ApolloPass);
            if (c is null) return;
            _padMouse.Suspended = c == true; // noen strømmer fra denne PC-en: ikke la kontrolleren styre musepekeren her
            if (!(d.StreamVirtual || d.StreamHostScreensOff)) return;
            if (c == true) { _hostWasStreaming = true; return; }
            if (!_hostWasStreaming) return;
            _hostWasStreaming = false;
            await Task.Delay(4000); // la Apollo sette skjermene tilbake først
            if (await StreamService.AnyClientConnectedAsync(d.ApolloUser, d.ApolloPass) == true) { _hostWasStreaming = true; return; }
            StreamService.ResetGraphicsStack();
        };
        _hostWatch.Start();
    }

    /// <summary>Sørger for at Apollo kjører uten at du må åpne Streaming-siden. Spør om tillatelse (UAC) bare når den ikke kjører og dette ikke er gjort før.</summary>
    private async Task ApolloAutostartAsync(bool force)
    {
        var d = _settings.Data;
        if (StreamService.ApolloDir is null || d.ApolloUser.Length == 0) return;
        bool running;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("127.0.0.1", 47990).WaitAsync(TimeSpan.FromSeconds(2));
            running = true;
        }
        catch { running = false; }
        if (running && !_apolloSynced) { await Task.Delay(4000); _apolloSynced = true; await SyncApolloAsync(); }
        if (!force && (running || d.ApolloAutostartDone)) return;
        var (ok, msg) = await StreamService.EnsureApolloServiceAsync();
        if (ok) { d.ApolloAutostartDone = true; _settings.Save(); _apolloState = ""; }
        DispatcherQueue.TryEnqueue(() => { SetStreamMsg(msg); RebuildStreamPage(); });
    }

    private bool _apolloSynced;

    /// <summary>Bryterne i ZenLauncher er sannheten: sørger for at Apollo faktisk har samme innstillinger (virtuell skjerm på appene, skjermene av under strøm).
    /// Apollo kan ha blitt tilbakestilt eller endret på nettsiden, så vi sjekker hver gang appen starter og Apollo kjører.</summary>
    private async Task SyncApolloAsync()
    {
        var d = _settings.Data;
        if (d.ApolloUser.Length == 0) return;
        try
        {
            // Apper først (endrer ikke Apollo-prosessen), konfigurasjon sist (starter Apollo på nytt).
            bool appsOk = true;
            var (s2, b2) = await StreamService.ApiAsync(HttpMethod.Get, "/api/apps", d.ApolloUser, d.ApolloPass, null, true);
            if (s2 == 200)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(b2);
                foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
                    if (!(a.TryGetProperty("virtual-display", out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False && v.GetBoolean() == d.StreamVirtual))
                    { appsOk = false; break; }
            }
            if (!appsOk)
            {
                var (_, m) = await StreamService.SetDesktopVirtualAsync(d.StreamVirtual, d.ApolloUser, d.ApolloPass);
                StreamService.Log("Synk: virtuell skjerm på appene. " + m);
            }

            var (s, b) = await StreamService.ApiAsync(HttpMethod.Get, "/api/config", d.ApolloUser, d.ApolloPass, null, true);
            if (s != 200) return;
            string want = d.StreamHostScreensOff ? "ensure_only_display" : "disabled";
            string have = "";
            bool haveHeadless = false;
            using (var doc = System.Text.Json.JsonDocument.Parse(b))
            {
                if (doc.RootElement.TryGetProperty("dd_configuration_option", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) have = v.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("headless_mode", out var hv))
                    haveHeadless = hv.ValueKind == System.Text.Json.JsonValueKind.True
                        || (hv.ValueKind == System.Text.Json.JsonValueKind.String && (hv.GetString() ?? "") is "enabled" or "true" or "on" or "1");
            }
            // «Headless mode» i Apollo tvinger ALLE strømmer over på den virtuelle skjermen, uansett hvilken app som startes.
            bool wantHeadless = d.StreamVirtual;
            var changes = new Dictionary<string, string>();
            if (have != want) changes["dd_configuration_option"] = want;
            if (haveHeadless != wantHeadless) changes["headless_mode"] = wantHeadless ? "enabled" : "disabled";
            if (changes.Count > 0)
            {
                var (_, m) = await StreamService.SetConfigAsync(changes, d.ApolloUser, d.ApolloPass);
                StreamService.Log($"Synk: skjermvalg var «{have}» (ønsket «{want}»), headless var {haveHeadless} (ønsket {wantHeadless}). {m}");
            }
        }
        catch (Exception ex) { StreamService.Log("Synk feilet: " + ex.Message); }
    }

    private async Task RefreshApolloAsync()
    {
        var d = _settings.Data;
        string st;
        var (s, _) = await StreamService.ApiAsync(HttpMethod.Get, "/api/config", d.ApolloUser, d.ApolloPass);
        if (s == 200)
        {
            st = "Kjører";
            _pairedClients = await StreamService.PairedClientsAsync(d.ApolloUser, d.ApolloPass);
        }
        else st = s == 401 ? "Innlogging mangler eller er feil. Trykk Sett opp." : "Kjører ikke. Trykk «Start automatisk» (starter Apollo-tjenesten, ett Windows-spørsmål).";
        _apolloState = st;
        RebuildStreamPage();
        if (s == 200 && !_apolloSynced) { _apolloSynced = true; await SyncApolloAsync(); }
    }

    private async Task RefreshDriverAsync()
    {
        var ok = await StreamService.VirtualDriverPresentAsync();
        if (ok != _driverOk) { _driverOk = ok; RebuildStreamPage(); }
    }

    private async Task InstallApolloUiAsync()
    {
        if (_streamBusy) return;
        _streamBusy = true;
        var d = _settings.Data;
        try
        {
            if (StreamService.ApolloDir is null)
            {
                var (ok, msg) = await StreamService.InstallApolloAsync("", "", SetStreamMsg);
                if (!ok) { SetStreamMsg(msg); return; }
                SetStreamMsg(msg + " Venter på at Apollo starter…");
                await Task.Delay(8000);
            }
            var user = "zenlauncher"; var pass = StreamService.NewPassword();
            SetStreamMsg("Lager innlogging…");
            var cr = await StreamService.SetupCredsAsync(user, pass);
            if (!cr.Ok) { SetStreamMsg(cr.Msg); return; }
            d.ApolloUser = user; d.ApolloPass = pass; _settings.Save();

            SetStreamMsg("Setter opp Apollo…");
            await Task.Delay(1500);
            var cfg = await StreamService.SetConfigAsync(new Dictionary<string, string>
            {
                ["origin_web_ui_allowed"] = "pc",
                ["dd_configuration_option"] = d.StreamHostScreensOff ? "ensure_only_display" : "disabled",
            }, user, pass);
            (bool Ok, string Msg) v = (true, "");
            if (d.StreamVirtual)
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(4000);
                    v = await StreamService.SetDesktopVirtualAsync(true, user, pass);
                    if (v.Ok) break;
                }
            SetStreamMsg("Apollo er satt opp. " + cfg.Msg + " " + v.Msg);
        }
        catch (Exception ex)
        {
            StreamService.Log("InstallApolloUi: " + ex);
            SetStreamMsg("Feil under oppsett: " + ex.Message);
        }
        finally
        {
            _streamBusy = false; _apolloState = ""; _driverKnown = false;
            RebuildStreamPage();
        }
    }

    private async Task ApplyVirtualAsync(bool on)
    {
        var d = _settings.Data;
        d.StreamVirtual = on; _settings.Save();
        var (_, msg) = await StreamService.SetDesktopVirtualAsync(on, d.ApolloUser, d.ApolloPass);
        var (_, msg2) = await StreamService.SetConfigAsync(new Dictionary<string, string> { ["headless_mode"] = on ? "enabled" : "disabled" }, d.ApolloUser, d.ApolloPass);
        SetStreamMsg(on ? "Alle strømmer bruker nå virtuell skjerm. " + msg2 : msg + " " + msg2);
    }

    private async Task ApplyScreensAsync(bool on)
    {
        var d = _settings.Data;
        d.StreamHostScreensOff = on; _settings.Save();
        var (_, msg) = await StreamService.SetConfigAsync(new Dictionary<string, string> { ["dd_configuration_option"] = on ? "ensure_only_display" : "disabled" }, d.ApolloUser, d.ApolloPass);
        SetStreamMsg(msg);
    }

    private async Task SubmitPinUiAsync(string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsDigit)) { SetStreamMsg("PIN-en er 4 siffer."); return; }
        var d = _settings.Data;
        var (ok, msg) = await StreamService.SubmitPinAsync(pin, "Klient " + (_pairedClients.Count + 1), d.ApolloUser, d.ApolloPass);
        SetStreamMsg(msg);
        if (ok) { _apolloState = ""; RebuildStreamPage(); }
    }

    private async Task RestartApolloUiAsync()
    {
        var d = _settings.Data;
        var (s, _) = await StreamService.ApiAsync(HttpMethod.Post, "/api/restart", d.ApolloUser, d.ApolloPass, new { });
        SetStreamMsg(s == 200 ? "Apollo starter på nytt." : "Kunne ikke starte på nytt (" + s + ").");
    }
}
