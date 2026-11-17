using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>Innstillingssiden «Verktøy og info»: om/hva er nytt, diagnose, sikkerhetskopi, statistikk, smarte lister, duplikater og lagringsplass.</summary>
public sealed partial class MainWindow
{
    private const int PageTools = 9;

    /// <summary>Kort liste over hva som er nytt i denne versjonen. Vises under «Hva er nytt».</summary>
    private static readonly string[] Changelog =
    {
        "Oppdateringssjekk mot GitHub, og installering av ny versjon med ett trykk.",
        "Wake-on-LAN: vekk verts-PC-en automatisk når du starter en strøm.",
        "Fortsett sist: start forrige strøm på nytt med ett trykk.",
        "Valgfri kvalitetsbegrensning når verten nås via Tailscale.",
        "Kopier brukernavn, passord og adresse på Streaming-siden.",
        "Sikkerhetskopi av innstillinger (uten passord), også automatisk én gang i døgnet.",
        "Diagnoseinfo og loggmappe for feilsøking.",
        "Statistikk over spilletid, smarte lister, duplikater og lagringsplass.",
        "Kontroller som mus pauses mens en strøm pågår.",
    };

    private static string Dur(long s) => s < 60 ? "0 min" : FormatDuration(s);

    private Dictionary<string, long>? _storageSizes;
    private bool _storageBusy;

    private void AddToolsHeader(string text) => SettingsBody.Children.Add(new TextBlock
    {
        Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = Br(255, 255, 255, 255), Margin = new Thickness(0, 18, 0, 4),
    });

    private void CopyToClipboard(string text, string toast)
    {
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            ShowToast(toast);
        }
        catch (Exception ex) { ShowToast("Kunne ikke kopiere: " + ex.Message); }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Alle kjente flis-nøkler med navn (spill, butikkspill og egne apper).</summary>
    private Dictionary<string, string> AllNames()
    {
        var names = new Dictionary<string, string>();
        foreach (var t in _gameTiles.Concat(_storeTiles)) names[t.Key] = t.Name;
        foreach (var a in _settings.Data.Apps) names["app:" + a.Target] = a.Name;
        return names;
    }

    private static string NameFor(string key, Dictionary<string, string> names) =>
        names.TryGetValue(key, out var n) ? n : (key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key);

    private static string JoinNames(List<string> list, int max = 8) =>
        list.Count == 0 ? "ingen" : string.Join(", ", list.Take(max)) + (list.Count > max ? $" … og {list.Count - max} til" : "");

    private void BuildToolsPage()
    {
        var d = _settings.Data;
        var names = AllNames();

        // ---- Om ----
        AddRow("ZenLauncher " + AppVersion, "Laget av ZenForge.", new TextBlock { Text = "" });
        AddToolsHeader("Hva er nytt");
        foreach (var line in Changelog) AddNote("•  " + line);

        // ---- Oppdateringer ----
        AddToolsHeader("Oppdateringer");
        BuildUpdateRows(d);

        // ---- Diagnose ----
        AddToolsHeader("Feilsøking");
        AddButton("Diagnoseinfo", "Kopierer versjon, Windows, oppsett og siste logglinjer. Ingen passord er med. Lim det inn når du rapporterer en feil.", "Kopier",
            () => CopyToClipboard(ToolsService.DiagReport(AppVersion, d), "Diagnoseinfo kopiert."));
        AddButton("Loggmappe", "Åpner mappen med stream.log og innstillinger.", "Åpne", () => OpenFolder(ToolsService.DataDir));

        // ---- Sikkerhetskopi ----
        AddToolsHeader("Sikkerhetskopi av innstillinger");
        AddToggle("Automatisk kopi", "Tar en kopi av innstillingene høyst én gang i døgnet ved oppstart. De 10 siste beholdes. Passord og nøkler er ikke med.", d.AutoBackup,
            v => { d.AutoBackup = v; _settings.Save(); });
        AddButton("Ta kopi nå", ToolsService.BackupCount() + " kopier lagret.", "Ta kopi", () =>
        {
            var (ok, msg) = ToolsService.Backup();
            if (ok) { d.LastBackup = NowUnix(); _settings.Save(); }
            ShowToast(msg);
            BuildSettingsBody();
        });
        AddButton("Gjenopprett nyeste kopi", "Legger kopien tilbake og lukker ZenLauncher. Start den på nytt etterpå. Innlogging og nøkler beholdes.", "Gjenopprett", () =>
        {
            var (ok, msg) = ToolsService.RestoreLatest(d);
            ShowToast(msg);
            if (ok) { var t = DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromSeconds(2); t.IsRepeating = false; t.Tick += (_, _) => ExitApp(); t.Start(); }
        });
        AddButton("Kopimappen", "Åpner mappen med kopiene.", "Åpne", () => OpenFolder(ToolsService.BackupDir));

        // ---- Statistikk ----
        AddToolsHeader("Statistikk");
        BuildStatsRows(d, names);

        // ---- Smarte lister ----
        AddToolsHeader("Smarte lister");
        BuildSmartLists(d, names);

        // ---- Duplikater ----
        AddToolsHeader("Spill i flere butikker");
        var dupes = _gameTiles.Concat(_storeTiles)
            .Where(t => t.Kind != TileKind.Add && !t.NotInstalled && t.StoreName.Length > 0)
            .GroupBy(t => new string(t.Name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()))
            .Where(g => g.Key.Length > 0 && g.Select(x => x.StoreName).Distinct().Count() > 1)
            .Select(g => g.First().Name + " (" + string.Join(" + ", g.Select(x => x.StoreName).Distinct()) + ")")
            .OrderBy(x => x).ToList();
        AddNote(dupes.Count == 0 ? "Ingen spill funnet i flere butikker." : dupes.Count + " spill finnes i flere butikker: " + JoinNames(dupes, 12));

        // ---- Lagringsplass ----
        AddToolsHeader("Lagringsplass");
        BuildStorageRows(names);
    }

    private void BuildStatsRows(SettingsData d, Dictionary<string, string> names)
    {
        long total = d.PlaySeconds.Values.Sum();
        AddNote("Total spilletid (spill startet fra ZenLauncher): " + (total > 0 ? Dur(total) : "ingen ennå"));

        var top = d.PlaySeconds.Where(x => x.Value > 0).OrderByDescending(x => x.Value).Take(5).ToList();
        if (top.Count > 0)
            AddNote("Mest spilt: " + string.Join(", ", top.Select(x => NameFor(x.Key, names) + " (" + Dur(x.Value) + ")")));

        var now = DateTimeOffset.Now;
        var days = new HashSet<DateTime>();
        long last7 = 0, last30 = 0;
        foreach (var list in d.Sessions.Values)
            foreach (var s in list)
            {
                if (s.Length < 2) continue;
                var start = DateTimeOffset.FromUnixTimeSeconds(s[0]).ToLocalTime();
                days.Add(start.Date);
                var age = (now - start).TotalDays;
                if (age <= 7) last7 += s[1];
                if (age <= 30) last30 += s[1];
            }
        if (days.Count == 0)
        {
            AddNote("Uke- og månedsoversikt og dager på rad bygges av pluginen «Spilleøkter» (Innstillinger → Plugins). Historikk finnes først fra du slår den på.");
            return;
        }
        int streak = 0;
        var day = now.Date;
        if (!days.Contains(day)) day = day.AddDays(-1);
        while (days.Contains(day)) { streak++; day = day.AddDays(-1); }
        AddNote($"Siste 7 dager: {Dur(last7)}   Siste 30 dager: {Dur(last30)}   Dager på rad: {streak}");
    }

    private void BuildSmartLists(SettingsData d, Dictionary<string, string> names)
    {
        var installed = _gameTiles.Concat(_storeTiles).Where(t => t.Kind != TileKind.Add && !t.NotInstalled).ToList();
        long nowU = NowUnix();
        long half = 180L * 86400;

        var never = installed.Where(t => !d.PlaySeconds.TryGetValue(t.Key, out var s) || s <= 0).Select(t => t.Name).OrderBy(x => x).ToList();
        var stale = installed.Where(t => d.PlaySeconds.TryGetValue(t.Key, out var s) && s > 0
                && d.LastLaunch.TryGetValue(t.Key, out var l) && nowU - l > half).Select(t => t.Name).OrderBy(x => x).ToList();
        var shortPlayed = installed.Where(t => d.PlaySeconds.TryGetValue(t.Key, out var s) && s > 0 && s < 7200).Select(t => t.Name).OrderBy(x => x).ToList();

        AddNote("Aldri spilt fra ZenLauncher (" + never.Count + "): " + JoinNames(never));
        AddNote("Ikke spilt på over 6 måneder (" + stale.Count + "): " + JoinNames(stale));
        AddNote("Under 2 timer spilt (" + shortPlayed.Count + "): " + JoinNames(shortPlayed));
    }

    private void BuildStorageRows(Dictionary<string, string> names)
    {
        if (_storageSizes is null)
        {
            AddButton("Beregn størrelser", "Måler hvor mye plass hver installerte spillmappe tar. Kan ta litt tid.", _storageBusy ? "Måler…" : "Beregn", () => _ = ComputeStorageAsync());
            return;
        }
        long sum = _storageSizes.Values.Sum();
        var top = _storageSizes.OrderByDescending(x => x.Value).Take(10).ToList();
        AddNote("Totalt " + FormatSize(sum) + " i " + _storageSizes.Count + " spillmapper. Størst: " +
                string.Join(", ", top.Select(x => NameFor(x.Key, names) + " " + FormatSize(x.Value))));
        AddButton("Mål på nytt", null, "Beregn", () => { _storageSizes = null; _ = ComputeStorageAsync(); });
    }

    private async Task ComputeStorageAsync()
    {
        if (_storageBusy) return;
        _storageBusy = true;
        RefreshToolsPage();
        var items = _gameTiles.Concat(_storeTiles)
            .Where(t => !t.NotInstalled)
            .Select(t => (t.Key, Dir: t.Game?.InstallPath ?? t.InstallDir ?? ""))
            .Where(x => x.Dir.Length > 0 && Directory.Exists(x.Dir)).ToList();
        var result = await Task.Run(() =>
        {
            var r = new Dictionary<string, long>();
            foreach (var (key, dir) in items)
            {
                long size = 0;
                try
                {
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                        try { size += f.Length; } catch { }
                }
                catch { }
                r[key] = size;
            }
            return r;
        });
        _storageSizes = result;
        _storageBusy = false;
        RefreshToolsPage();
    }

    private void RefreshToolsPage()
    {
        if (SettingsPage.Visibility == Visibility.Visible && _settingsPage == PageTools) BuildSettingsBody();
    }

    /// <summary>Ved oppstart: automatisk sikkerhetskopi (høyst én gang i døgnet) og «oppdatert»-melding etter ny versjon.</summary>
    private async Task StartupToolsAsync()
    {
        await Task.Delay(5000);
        var d = _settings.Data;
        if (d.AutoBackup && NowUnix() - d.LastBackup > 20L * 3600)
        {
            var (ok, _) = await Task.Run(ToolsService.Backup);
            if (ok) { d.LastBackup = NowUnix(); _settings.Save(); }
        }
        if (d.LastRunVersion != AppVersion)
        {
            if (d.LastRunVersion.Length > 0) ShowToast($"ZenLauncher er oppdatert til {AppVersion}. Se Innstillinger → Verktøy og info.");
            d.LastRunVersion = AppVersion;
            _settings.Save();
        }
    }
}
