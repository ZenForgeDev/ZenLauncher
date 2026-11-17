using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ZenStationShell;

/// <summary>
/// Desktop-modus: innstillinger som en egen side (kategorier til venstre, vanlige kontroller til høyre) i stedet for en meny.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly string[] SettingsPages = { "Generelt", "Visning", "Kontroller", "Strøm og dvale", "System", "Plugins", "Nedlastinger", "Samlinger", "Streaming", "Verktøy og info" };
    private int _settingsPage;
    private bool _buildingSettings;

    private static SolidColorBrush Br(byte a, byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(a, r, g, b));

    private void ShowSettingsPage(int page = 0)
    {
        _settingsPage = page;
        _mode = Mode.Busy; // blokkerer snarveier mot spillene bak siden
        SettingsPage.Visibility = Visibility.Visible;
        _buildingSettings = true;
        if (SettingsNav.Items.Count == 0) foreach (var n in SettingsPages) SettingsNav.Items.Add(n);
        SettingsNav.SelectedIndex = page;
        _buildingSettings = false;
        BuildSettingsBody();
        SettingsNav.Focus(FocusState.Programmatic);
    }

    private void CloseSettingsPage()
    {
        if (SettingsPage.Visibility != Visibility.Visible) return;
        SettingsPage.Visibility = Visibility.Collapsed;
        _mode = Mode.Home;
        GameList.Focus(FocusState.Programmatic);
    }

    private void DesktopSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        ShowSettingsPage();
    }

    private void SettingsClose_Click(object sender, RoutedEventArgs e) => CloseSettingsPage();
    private void SettingsPage_Tapped(object sender, TappedRoutedEventArgs e) => CloseSettingsPage();
    private void SettingsPanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void SettingsPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseSettingsPage();
        }
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_buildingSettings || SettingsNav.SelectedIndex < 0) return;
        _settingsPage = SettingsNav.SelectedIndex;
        BuildSettingsBody();
    }

    // ---------- Innhold ----------

    private void BuildSettingsBody()
    {
        _buildingSettings = true;
        SettingsBody.Children.Clear();
        SettingsTitle.Text = SettingsPages[_settingsPage];
        var d = _settings.Data;
        switch (_settingsPage)
        {
            case 0: // Generelt
                AddToggle("Desktop-modus", "Vanlig vindu for mus og tastatur. Av gir fullskjerm for kontroller (konsollmodus).", d.DesktopMode, v =>
                {
                    d.DesktopMode = v;
                    _settings.Save();
                    if (!v) CloseSettingsPage();
                    ApplyWindowMode();
                });
                AddToggle("Vis Skjult-fanen", "Når av, vises skjulte spill ingen steder. Når på, kan de hentes fram igjen.", d.ShowHiddenTab, SetShowHiddenTab);
                AddButton("Kontoer", "Epic-innlogging og bibliotek.", "Åpne…", () => OpenFromSettings("Kontoer", BuildAccountsMenu));
                AddButton("Lyd", "Appvolum, lydutgang og menylyder.", "Åpne…", () => OpenFromSettings("Lyd", BuildSoundMenu));
                AddButton("Bibliotek", "Søk etter nye spill og legg til apper.", "Åpne…", () => OpenFromSettings("Bibliotek", BuildLibraryMenu));
                BuildUpdateRows(d);
                break;

            case 1: // Visning
                AddToggle("Parallakse", "Heltebildet flytter seg litt når du blar (konsollmodus).", d.Parallax, v =>
                {
                    d.Parallax = v;
                    _settings.Save();
                    ApplyParallax();
                });
                AddCombo("Rammefarge", "Ramme rundt valgt flis i konsollmodus. Av og enkeltfarge sparer CPU.",
                    FrameThemes.Select(f => f.Name).ToArray(), Math.Clamp(d.FrameTheme, 0, FrameThemes.Length - 1), i =>
                    {
                        d.FrameTheme = i;
                        _settings.Save();
                        ApplyFrameTheme();
                    });
                AddCombo("Dimming", "Skjermen mørkner når du er inaktiv.",
                    DimSteps.Select(MinText).ToArray(), Math.Max(0, Array.IndexOf(DimSteps, d.DimMinutes)), i =>
                    {
                        d.DimMinutes = DimSteps[i];
                        _settings.Save();
                        UpdateIdleTimer();
                    });
                break;

            case 2: // Kontroller
                AddToggle("Kontroller som mus", "Styr musepekeren i andre apper med kontrolleren. Back + Start slår det av og på.", d.PadMouse, v =>
                {
                    d.PadMouse = v;
                    _padMouse.Enabled = v;
                    _settings.Save();
                });
                break;

            case 3: // Strøm og dvale
                AddCombo("Auto-hvilemodus", "PC-en går i hvilemodus når du er inaktiv.",
                    SleepSteps.Select(MinText).ToArray(), Math.Max(0, Array.IndexOf(SleepSteps, d.SleepMinutes)), i =>
                    {
                        d.SleepMinutes = SleepSteps[i];
                        _settings.Save();
                        UpdateIdleTimer();
                    });
                AddButton("Strøm", "Hvilemodus, start på nytt, slå av og avslutt.", "Åpne…", () => OpenFromSettings("Strøm", BuildPowerMenu));
                break;

            case 5: // Plugins
                AddNote("Plugins er valgfrie tillegg. Alle er av som standard. Noen sender spillnavn til en nettside, og kan slutte å virke hvis nettsiden endrer seg.");
                AddToggle("HowLongToBeat (eksperimentell)",
                    "Viser hvor lang tid et spill tar å fullføre i Detaljer-kortet. Uoffisiell: henter fra howlongtobeat.com (spillnavnet sendes dit), og kan slutte å virke.",
                    PluginOn(Plugins.HowLongToBeat.Id), v => SetPlugin(Plugins.HowLongToBeat.Id, v));
                AddToggle("Spilleøkter", "Logger hver gang du spiller (dato og varighet) og viser de siste i Detaljer. Lagres lokalt. Historikk finnes først fra du slår den på.",
                    PluginOn("sessions"), v => SetPlugin("sessions", v));
                AddToggle("Notater", "Et notatfelt per spill på detaljsiden (desktop-modus). Lagres lokalt.",
                    PluginOn("notes"), v => SetPlugin("notes", v));
                AddToggle("Egne skjermbilder", "Viser dine egne Steam-skjermbilder for spillet på detaljsiden. Leses fra Steam-mappen, ingen nettverk.",
                    PluginOn(Plugins.LocalShots.Id), v => SetPlugin(Plugins.LocalShots.Id, v));
                AddToggle("Tilfeldig spill", "Legger en 🎲-knapp øverst som velger et tilfeldig installert spill fra listen du ser på.",
                    PluginOn("random"), v => SetPlugin("random", v));
                break;

            case PageDownloads: BuildDownloadsPage(); break;
            case PageCollections: BuildCollectionsPage(); break;
            case PageStreaming: BuildStreamingPage(); break;
            case PageTools: BuildToolsPage(); break;

            case 4: // System
                AddToggle("Start med Windows", "Starter ZenLauncher når du logger inn.", d.StartWithWindows, v => { d.StartWithWindows = v; StartupChanged(); });
                AddToggle("Rask oppstart", "Bruker Oppgaveplanlegger (Task Scheduler) i stedet for vanlig oppstart: ingen forsinkelse etter innlogging. Krever «Start med Windows».", d.FastStartup, v => { d.FastStartup = v; StartupChanged(); });
                AddToggle("Start skjult i systemkurven", "Appen starter uten vindu og ligger ved klokken til du åpner den.", d.StartInTray, v => { d.StartInTray = v; StartupChanged(); });
                AddToggle("Lukk til systemkurven", "Kryss-knappen skjuler appen i stedet for å avslutte den. Høyreklikk ikonet ved klokken for å avslutte helt.", d.CloseToTray, v => { d.CloseToTray = v; StartupChanged(); });
                AddButton("Windows og system", "Skjerm, nettverk, Bluetooth og Windows Update.", "Åpne…", () => OpenFromSettings("Windows og system", BuildSystemMenu));
                break;
        }
        _buildingSettings = false;
    }

    /// <summary>Versjonen fra pakken (Package.appxmanifest). Avkortet: 1.2.0.0 vises som 1.2.</summary>
    private static string AppVersion
    {
        get
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                var p = new List<int> { v.Major, v.Minor, v.Build, v.Revision };
                while (p.Count > 2 && p[^1] == 0) p.RemoveAt(p.Count - 1);
                return string.Join(".", p);
            }
            catch
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "?" : $"{v.Major}.{v.Minor}";
            }
        }
    }

    private bool PluginOn(string id) => _settings.Data.Plugins.TryGetValue(id, out var on) && on;

    private void SetPlugin(string id, bool on)
    {
        _settings.Data.Plugins[id] = on;
        _settings.Save();
        _infoTile = null; // last detaljene på nytt
        ApplyPluginUi();
        if (DesktopOn && GameList.SelectedItem is TileVm t) SyncDesktopDetail(t);
    }

    /// <summary>Viser/skjuler knapper og felt som hører til plugins.</summary>
    private void ApplyPluginUi()
    {
        DesktopRandomBtn.Visibility = PluginOn("random") ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordSession(string key, long secs)
    {
        var d = _settings.Data;
        if (!d.Sessions.TryGetValue(key, out var list)) d.Sessions[key] = list = new List<long[]>();
        list.Add(new[] { NowUnix() - secs, secs });
        if (list.Count > 30) list.RemoveRange(0, list.Count - 30);
    }

    private void DesktopRandom_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        PickRandom();
    }

    private void PickRandom()
    {
        var pool = _tiles.Where(x => x.Kind != TileKind.Add && !x.NotInstalled).ToList();
        if (pool.Count == 0) { ShowToast("Ingen spill å velge mellom i denne listen."); return; }
        var pick = pool[Random.Shared.Next(pool.Count)];
        GameList.SelectedItem = pick;
        ShowToast($"Tilfeldig: {pick.Name}");
    }

    private string? _notesKey;

    private void DesktopNotes_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_notesKey is null) return;
        var txt = DesktopNotesBox.Text.TrimEnd();
        var notes = _settings.Data.Notes;
        bool had = notes.TryGetValue(_notesKey, out var old);
        if (txt.Length == 0) { if (had) { notes.Remove(_notesKey); _settings.Save(); } }
        else if (!had || old != txt) { notes[_notesKey] = txt; _settings.Save(); }
    }

    private void AddNote(string text) => SettingsBody.Children.Add(new TextBlock
    {
        Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Br(0x99, 255, 255, 255), Margin = new Thickness(0, 4, 0, 8),
    });

    private void OpenFromSettings(string title, Func<List<MenuEntry>> build)
    {
        CloseSettingsPage();
        _flyAnchor = null; _flyPoint = null;
        OpenMenu(title, build);
    }

    private void AddRow(string title, string? desc, FrameworkElement control)
    {
        var g = new Grid { ColumnSpacing = 24, Padding = new Thickness(0, 14, 0, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 16, Foreground = Br(255, 255, 255, 255) });
        if (!string.IsNullOrEmpty(desc))
            left.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = Br(0x99, 255, 255, 255), TextWrapping = TextWrapping.Wrap });
        g.Children.Add(left);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        SettingsBody.Children.Add(new Border
        {
            BorderBrush = Br(0x22, 255, 255, 255),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = g,
        });
    }

    private void AddToggle(string title, string? desc, bool on, Action<bool> set)
    {
        var ts = new ToggleSwitch { IsOn = on, OnContent = "På", OffContent = "Av", MinWidth = 0 };
        ts.Toggled += (_, _) => { if (!_buildingSettings) set(ts.IsOn); };
        AddRow(title, desc, ts);
    }

    private void AddCombo(string title, string? desc, string[] items, int selected, Action<int> set)
    {
        var cb = new ComboBox { MinWidth = 180, CornerRadius = new CornerRadius(8) };
        foreach (var it in items) cb.Items.Add(it);
        cb.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, items.Length - 1));
        cb.SelectionChanged += (_, _) => { if (!_buildingSettings && cb.SelectedIndex >= 0) set(cb.SelectedIndex); };
        AddRow(title, desc, cb);
    }

    private void AddButton(string title, string? desc, string label, Action click)
    {
        var b = new Button { Content = label, MinWidth = 110, CornerRadius = new CornerRadius(8) };
        b.Click += (_, _) => click();
        AddRow(title, desc, b);
    }
}
