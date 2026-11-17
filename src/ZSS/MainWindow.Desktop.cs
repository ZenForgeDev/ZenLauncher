using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ZenStationShell.Services;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;
using ZenTune.Core.Presets;

namespace ZenStationShell;

/// <summary>
/// Desktop-modus: bibliotek med liste til venstre og detaljer til høyre, for mus og tastatur (som Steams vanlige visning).
/// Bruker de samme flisene, filtrene og menyene som konsollvisningen. GameList er fortsatt «sannheten» for valgt flis;
/// DesktopList speiler den.
/// </summary>
public sealed partial class MainWindow
{
    private bool _syncingDesktop;
    /// <summary>Utvalget kom fra et klikk i listen/rutenettet: da skal vi ikke rulle (ga hopping).</summary>
    private bool _mouseSelect;

    private bool DesktopOn => _settings.Data.DesktopMode;

    /// <summary>Viser enten konsollvisningen (HomeLayer) eller desktop-visningen, og ingen av dem når appen ikke har fokus.</summary>
    private void ApplyLayerVisibility()
    {
        bool d = DesktopOn;
        // Lagene skjules ikke lenger uten fokus (det ga svart vindu i Oppgavevisning/Alt+Tab). CPU spares ved at
        // kontroller, klokke og regnbueanimasjon pauses i SetWindowActive.
        HomeLayer.Visibility = !d ? Visibility.Visible : Visibility.Collapsed;
        HeroImage.Visibility = !d ? Visibility.Visible : Visibility.Collapsed;
        DesktopLayer.Visibility = d ? Visibility.Visible : Visibility.Collapsed;
        if (d) { ApplyDesktopView(); ApplyPluginUi(); SyncDesktopFilter(); }
        if (d && GameList.SelectedItem is TileVm t) SyncDesktopDetail(t);
    }

    private static readonly string[] FilterNames = { "Alle", "★ Favoritter", "Sist spilt", "Apper", "Ikke installert", "Skjult", "Gratis", "Streaming" };

    /// <summary>Holder søkefeltet og kategorilisten i takt med _search og _filter.</summary>
    private void SyncDesktopFilter()
    {
        if (DesktopFilter is null) return;
        _syncingDesktop = true;
        try
        {
            var wanted = new List<(string name, int filter)>();
            for (int i = 0; i < FilterNames.Length; i++)
            {
                if (i == FHidden && !_settings.Data.ShowHiddenTab) continue;
                wanted.Add((FilterNames[i], i));
            }
            int c = 0;
            foreach (var k in _settings.Data.Collections.Keys) wanted.Add((k, FBase + c++));

            bool same = DesktopFilter.Items.Count == wanted.Count;
            if (same)
                for (int i = 0; i < wanted.Count; i++)
                    if (DesktopFilter.Items[i] is not ComboBoxItem cbi || (string?)cbi.Content != wanted[i].name || (int)cbi.Tag != wanted[i].filter) { same = false; break; }
            if (!same)
            {
                DesktopFilter.Items.Clear();
                foreach (var w in wanted) DesktopFilter.Items.Add(new ComboBoxItem { Content = w.name, Tag = w.filter });
            }
            int sel = -1;
            if (_search.Length == 0)
                for (int i = 0; i < DesktopFilter.Items.Count; i++)
                    if (DesktopFilter.Items[i] is ComboBoxItem cbi && (int)cbi.Tag == _filter) { sel = i; break; }
            if (DesktopFilter.SelectedIndex != sel) DesktopFilter.SelectedIndex = sel;
            if (DesktopSort.Items.Count != SortNames.Length)
            {
                DesktopSort.Items.Clear();
                foreach (var sn in SortNames) DesktopSort.Items.Add("Sorter: " + sn);
            }
            if (DesktopSort.SelectedIndex != _settings.Data.SortMode) DesktopSort.SelectedIndex = _settings.Data.SortMode;
            if (DesktopSearch.Text != _search) DesktopSearch.Text = _search;
            int n = _tiles.Count(x => x.Kind != TileKind.Add);
            DesktopCount.Text = n == 1 ? "1 TREFF" : $"{n} TREFF";
            DesktopEmpty.Text = EmptyText.Text;
            DesktopEmpty.Visibility = EmptyText.Visibility;
        }
        finally { _syncingDesktop = false; }
    }

    private string PlayLabel(TileVm t) =>
        t.Kind == TileKind.Add ? "Legg til app"
        : _running is not null && _running.Key == t.Key ? "Tilbake til spillet"
        : t.NotInstalled ? "Installer"
        : t.Kind == TileKind.Game || t.Store is not null ? "Spill"
        : "Åpne";

    /// <summary>Oppdaterer høyre side og markeringen i listen for valgt flis (null = tomt).</summary>
    private void SyncDesktopDetail(TileVm? t)
    {
        if (DesktopLayer is null) return;
        _syncingDesktop = true;
        try
        {
            if (t is null)
            {
                DesktopTitle.Text = "";
                DesktopHero.Source = null;
                DesktopPosterCover.Source = null;
                DesktopPosterIcon.Source = null;
                DesktopPosterGlyph.Text = "";
                DesktopStoreBadge.Visibility = Visibility.Collapsed;
                DesktopStat1.Visibility = DesktopStat2.Visibility = Visibility.Collapsed;
                DesktopPlay.IsEnabled = DesktopFav.IsEnabled = DesktopMore.IsEnabled = false;
                return;
            }
            DesktopTitle.Text = t.Name;
            DesktopStoreText.Text = t.StoreName.ToUpperInvariant();
            DesktopStoreBadge.Visibility = t.StoreName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Nøkkeltall: spilletid og sist spilt (skjules når de ikke finnes).
            long secs = _settings.Data.PlaySeconds.TryGetValue(t.Key, out var sc) ? sc : 0;
            string dur = FormatDuration(secs);
            long last = t.Kind == TileKind.Add ? 0 : LastPlayedOf(t);
            DesktopStat1.Visibility = dur.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            DesktopStat1Label.Text = "SPILLETID";
            DesktopStat1Value.Text = dur;
            DesktopStat2.Visibility = last > 0 ? Visibility.Visible : Visibility.Collapsed;
            DesktopStat2Label.Text = "SIST SPILT";
            DesktopStat2Value.Text = last > 0 ? LastPlayedText(last) : "";

            DesktopPosterCover.Source = t.Cover;
            DesktopPosterIcon.Source = t.Icon;
            DesktopPosterGlyph.Text = t.Icon is null && t.Cover is null ? t.Glyph : "";

            DesktopPlay.Content = PlayLabel(t);
            bool real = t.Kind != TileKind.Add;
            DesktopPlay.IsEnabled = true;
            DesktopFav.IsEnabled = DesktopMore.IsEnabled = real;
            DesktopFav.Content = t.Star.Length > 0 ? "★" : "☆";
            DesktopHero.Source = t.Hero;
            if (!ReferenceEquals(_infoTile, t)) _ = LoadDesktopInfoAsync(t);
            if (!ReferenceEquals(DesktopList.SelectedItem, t)) DesktopList.SelectedItem = t;
            if (!ReferenceEquals(DesktopGrid.SelectedItem, t)) DesktopGrid.SelectedItem = t;
            if (!_mouseSelect)
            {
                DesktopList.ScrollIntoView(t);
                DesktopGrid.ScrollIntoView(t);
            }
        }
        finally { _syncingDesktop = false; }
    }

    // ---------- Om spillet (sjangre, beskrivelse, skjermbilder) ----------

    private TileVm? _infoTile;

    private Border Chip(string text, bool accent = false) => new()
    {
        Padding = new Thickness(10, 4, 10, 4),
        CornerRadius = new CornerRadius(8),
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent
            ? Windows.UI.Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6) : Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) },
    };

    private static string FormatSize(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.#} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024} kB";

    private void OpenShot(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { ShowToast("Kunne ikke åpne bildet."); }
    }

    /// <summary>Høyreklikk på et eget skjermbilde: åpne, vis i mappe, kopier bilde eller sti.</summary>
    private void ShowShotMenu(string path, FrameworkElement anchor, Windows.Foundation.Point at)
    {
        var fly = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Åpne" };
        open.Click += (_, _) => OpenShot(path);
        var loc = new MenuFlyoutItem { Text = "Åpne filplassering" };
        loc.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch { ShowToast("Kunne ikke åpne mappen."); }
        };
        var copyImg = new MenuFlyoutItem { Text = "Kopier bilde" };
        copyImg.Click += async (_, _) =>
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ShowToast("Bildet er kopiert.");
            }
            catch { ShowToast("Kunne ikke kopiere bildet."); }
        };
        var copyPath = new MenuFlyoutItem { Text = "Kopier filsti" };
        copyPath.Click += (_, _) =>
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(path);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
                ShowToast("Filstien er kopiert.");
            }
            catch { }
        };
        fly.Items.Add(open);
        fly.Items.Add(loc);
        fly.Items.Add(new MenuFlyoutSeparator());
        fly.Items.Add(copyImg);
        fly.Items.Add(copyPath);
        fly.ShowAt(anchor, at);
    }

    private void AddFact(string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = 11, CharacterSpacing = 60, Foreground = Br(0x88, 255, 255, 255), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) });
        var v = new TextBlock { Text = value, FontSize = 14, Foreground = Br(255, 255, 255, 255), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        DesktopFacts.Children.Add(g);
    }

    private static string? DeckText(int c) => c switch { 3 => "Verifisert", 2 => "Spillbar", 1 => "Ikke støttet", _ => null };

    /// <summary>Detaljer-kortet. Egne tall (spilletid, sist spilt, samlinger) finnes for alle spill; resten fra Steam når det er hentet.</summary>
    private List<(string Label, string Value)> FactRows(TileVm t, StoreInfoData? info, StoreExtras? extra, long size, Plugins.HltbData? hltb = null)
    {
        var rows = new List<(string Label, string Value)>();
        if (t.Kind == TileKind.Add) return rows;
        void AddFact(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value)); }
        var d = _settings.Data;
        AddFact("Butikk", t.StoreName.Length > 0 ? t.StoreName : "Egen app");
        if (info is not null)
        {
            AddFact("Utvikler", info.Developer);
            AddFact("Utgitt", info.Released);
        }
        if (extra is not null)
        {
            AddFact("Anmeldelser", extra.Reviews);
            AddFact("Steam Deck", DeckText(extra.Deck));
        }
        if (hltb is not null)
        {
            var parts = new List<string>();
            if (hltb.Main > 0) parts.Add($"Hovedhistorie {hltb.Main:0.#} t");
            if (hltb.Extra > 0) parts.Add($"Hoved + ekstra {hltb.Extra:0.#} t");
            if (hltb.Complete > 0) parts.Add($"100 % {hltb.Complete:0.#} t");
            AddFact("Tid å fullføre", parts.Count > 0 ? string.Join("\n", parts) : null);
        }
        if (info is not null)
        {
            if (info.Achievements > 0) AddFact("Prestasjoner", info.Achievements.ToString());
            if (info.Categories.Count > 0) AddFact("Funksjoner", string.Join(", ", info.Categories));
        }
        long secs = d.PlaySeconds.TryGetValue(t.Key, out var sc) ? sc : 0;
        AddFact("Spilletid", secs > 0 ? FormatDuration(secs) : null);
        long last = LastPlayedOf(t);
        AddFact("Sist spilt", last > 0 ? LastPlayedText(last) : null);
        if (t.Game is { Installed: > 0 } g)
            AddFact("Installert", DateTimeOffset.FromUnixTimeSeconds(g.Installed).ToLocalTime().ToString("dd.MM.yyyy"));
        if (PluginOn("sessions") && d.Sessions.TryGetValue(t.Key, out var ses) && ses.Count > 0)
        {
            var lines = ses.AsEnumerable().Reverse().Take(5)
                .Select(x => DateTimeOffset.FromUnixTimeSeconds(x[0]).ToLocalTime().ToString("dd.MM HH:mm") + "  ·  " + (FormatDuration(x[1]) is { Length: > 0 } fd ? fd : "under 1 min"));
            AddFact("Siste økter", string.Join("\n", lines));
        }
        if (size > 0) AddFact("Størrelse", FormatSize(size));
        var cols = d.Collections.Where(kv => kv.Value.Contains(t.Key)).Select(kv => kv.Key).ToList();
        if (cols.Count > 0) AddFact("Samlinger", string.Join(", ", cols));
        var path = t.Game?.InstallPath ?? t.InstallDir;
        if (!string.IsNullOrEmpty(path)) AddFact("Mappe", path);
        return rows;
    }

    private void BuildFacts(TileVm t, StoreInfoData? info, StoreExtras? extra, long size, Plugins.HltbData? hltb = null)
    {
        DesktopFacts.Children.Clear();
        foreach (var (label, value) in FactRows(t, info, extra, size, hltb)) AddFact(label, value);
    }

    private async Task LoadDesktopInfoAsync(TileVm t)
    {
        _infoTile = t;
        DesktopChips.Children.Clear();
        DesktopShots.Children.Clear();
        DesktopMyShots.Children.Clear();
        DesktopMyShotsPanel.Visibility = Visibility.Collapsed;
        // Plugin: notater
        _notesKey = null;
        bool notesOn = PluginOn("notes") && t.Kind != TileKind.Add;
        DesktopNotes.Visibility = notesOn ? Visibility.Visible : Visibility.Collapsed;
        if (notesOn)
        {
            DesktopNotesBox.Text = _settings.Data.Notes.TryGetValue(t.Key, out var note) ? note : "";
            _notesKey = t.Key;
        }
        // Plugin: egne skjermbilder
        if (PluginOn(Plugins.LocalShots.Id) && t.Game is { } sg)
        {
            var files = Plugins.LocalShots.Find(sg.AppId);
            foreach (var f in files)
            {
                var host = new Grid { Width = 240, Height = 135, CornerRadius = new CornerRadius(10), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 27, 30, 42)) };
                host.Children.Add(new Microsoft.UI.Xaml.Controls.Image
                {
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(f)) { DecodePixelWidth = 320 },
                });
                var shotPath = f;
                host.Tapped += (_, _) => OpenShot(shotPath);
                host.RightTapped += (sender2, e2) =>
                {
                    e2.Handled = true;
                    ShowShotMenu(shotPath, (FrameworkElement)sender2, e2.GetPosition((UIElement)sender2));
                };
                ToolTipService.SetToolTip(host, "Klikk for å åpne, høyreklikk for flere valg");
                DesktopMyShots.Children.Add(host);
            }
            DesktopMyShotsPanel.Visibility = files.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        DesktopShotsScroll.Visibility = Visibility.Collapsed;
        DesktopDesc.Text = "";

        StoreInfoData? info = null;
        StoreExtras? extra = null;
        long size = 0;
        Plugins.HltbData? hltb = null;
        void Rebuild() { if (ReferenceEquals(_infoTile, t)) BuildFacts(t, info, extra, size, hltb); }
        Rebuild();

        var dir = t.Game?.InstallPath ?? t.InstallDir;
        var sizeTask = StoreInfo.FolderSizeAsync(dir);

        // Plugin (av som standard): HowLongToBeat for spill.
        Task? hltbTask = null;
        if (PluginOn(Plugins.HowLongToBeat.Id) && (t.Game is not null || t.Store is not null))
            hltbTask = Task.Run(async () =>
            {
                var r = await Plugins.HowLongToBeat.GetAsync(t.Name);
                DispatcherQueue.TryEnqueue(() => { hltb = r; Rebuild(); });
            });

        if (t.Game is not { } g)
        {
            DesktopInfo.Visibility = Visibility.Collapsed;
            size = await sizeTask;
            Rebuild();
            return;
        }
        DesktopInfo.Visibility = Visibility.Visible;

        var infoTask = StoreInfo.GetAsync(g.AppId);
        var extraTask = StoreInfo.ExtrasAsync(g.AppId);

        info = await infoTask;
        if (!ReferenceEquals(_infoTile, t)) return;
        if (info is not null)
        {
            foreach (var genre in info.Genres.Take(4)) DesktopChips.Children.Add(Chip(genre, true));
            if (info.Metacritic > 0) DesktopChips.Children.Add(Chip("Metacritic " + info.Metacritic));
            DesktopDesc.Text = info.Description;
            foreach (var url in info.Shots)
            {
                var host = new Grid { Width = 240, Height = 135, CornerRadius = new CornerRadius(10), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 27, 30, 42)) };
                host.Children.Add(new Microsoft.UI.Xaml.Controls.Image
                {
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)) { DecodePixelWidth = 320 },
                });
                DesktopShots.Children.Add(host);
            }
            DesktopShotsScroll.Visibility = info.Shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Rebuild();

        extra = await extraTask;
        size = await sizeTask;
        Rebuild();
    }

    // ---------- Hendelser fra desktop-visningen ----------

    private void DesktopList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDesktop || DesktopList.SelectedItem is not TileVm t) return;
        _mouseSelect = true;
        try { if (!ReferenceEquals(GameList.SelectedItem, t)) GameList.SelectedItem = t; }
        finally { _mouseSelect = false; } // gir GameList_SelectionChanged → SyncDesktopDetail
    }

    private void DesktopGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDesktop || DesktopGrid.SelectedItem is not TileVm t) return;
        _mouseSelect = true;
        try { if (!ReferenceEquals(GameList.SelectedItem, t)) GameList.SelectedItem = t; }
        finally { _mouseSelect = false; }
    }

    private void DesktopSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDesktop || DesktopSort.SelectedIndex < 0 || DesktopSort.SelectedIndex == _settings.Data.SortMode) return;
        _settings.Data.SortMode = DesktopSort.SelectedIndex;
        _settings.Save();
        RebuildTiles();
    }

    private void DesktopViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.Data.DesktopGrid = !_settings.Data.DesktopGrid;
        _settings.Save();
        ApplyDesktopView();
        if (GameList.SelectedItem is TileVm t) SyncDesktopDetail(t);
    }

    /// <summary>Liste + detaljer, eller rutenett. I rutenettet er sidepanelet smalere og detaljsiden skjult.</summary>
    private void ApplyDesktopView()
    {
        bool grid = _settings.Data.DesktopGrid;
        DesktopList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        DesktopDetail.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        DesktopGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        DesktopLayer.ColumnDefinitions[0].Width = new GridLength(grid ? 250 : 360);
        DesktopViewToggle.Content = grid ? "☰" : "▦";
    }

    private void DesktopList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Start bare når dobbeltklikket traff selve spillet/appen, ikke tomrom, rullefelt eller kanter i listen.
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not TileVm t) return;
        if (!ReferenceEquals(GameList.SelectedItem, t)) GameList.SelectedItem = t;
        _ = AcceptAsync();
    }

    private void DesktopList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TileVm t)
        {
            GameList.SelectedItem = t;
            e.Handled = true;
            if (t.Kind == TileKind.Add) { OpenGameMenu(); return; }
            ShowGameContextMenu(t, (FrameworkElement)sender, e.GetPosition((UIElement)sender));
        }
    }

    private void DesktopSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingDesktop || DesktopSearch.Text == _search) return;
        _search = DesktopSearch.Text;
        RebuildTiles("");
    }

    private void DesktopSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                DesktopSearch.Text = "";
                DesktopList.Focus(FocusState.Keyboard);
                break;
            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                DesktopList.Focus(FocusState.Keyboard);
                break;
        }
    }

    private void DesktopFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDesktop || DesktopFilter.SelectedItem is not ComboBoxItem cbi) return;
        _filter = (int)cbi.Tag;
        _search = "";
        RebuildTiles("");
    }

    private void DesktopPlay_Click(object sender, RoutedEventArgs e) => _ = AcceptAsync();

    private void DesktopFav_Click(object sender, RoutedEventArgs e)
    {
        ToggleFavorite();
        if (GameList.SelectedItem is TileVm t) SyncDesktopDetail(t);
    }

    private void DesktopMore_Click(object sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is TileVm { Kind: not TileKind.Add } t) ShowGameContextMenu(t, DesktopMore, null);
    }

    /// <summary>Vanlig høyreklikkmeny (som i Steam/Epic) med de samme valgene som konsollmenyen.</summary>
    private void ShowGameContextMenu(TileVm t, FrameworkElement anchor, Windows.Foundation.Point? at)
    {
        if (_mode != Mode.Home) return;
        _flyAnchor = anchor; _flyPoint = at;
        var fly = new MenuFlyout();
        foreach (var entry in BuildGameMenu(t))
        {
            if (entry.OnAccept is null) continue;
            var m = entry;
            var item = new MenuFlyoutItem { Text = m.Title };
            if (m.Detail.Length > 0) item.KeyboardAcceleratorTextOverride = m.Detail;
            item.Click += async (_, _) =>
            {
                try { await m.OnAccept!(); } catch { }
            };
            fly.Items.Add(item);
        }
        if (fly.Items.Count == 0) return;
        if (at is { } p) fly.ShowAt(anchor, p); else fly.ShowAt(anchor);
    }

    // ---------- Desktop: alle menyer som vanlige flyout-menyer (som høyreklikkmenyen) ----------

    private MenuFlyout? _flyout;
    private FrameworkElement? _flyAnchor;
    private Windows.Foundation.Point? _flyPoint;
    private int _flyGen;
    /// <summary>Disse er lange innstillingslister og vises som en bred side i stedet for en flyout.</summary>
    private static readonly string[] PageMenus = { "Innstillinger", "Windows og system" };

    private bool UseFlyoutMenu() => DesktopOn && _menus.Count > 0 && Array.IndexOf(PageMenus, _menus.Peek().Item1) < 0;

    private void ShowFlyoutMenu()
    {
        if (_menus.Count == 0) return;
        int gen = ++_flyGen;
        MenuOverlay.Visibility = Visibility.Collapsed;
        var old = _flyout; _flyout = null;
        try { old?.Hide(); } catch { }
        // Vises litt senere slik at forrige flyout rekker å lukkes.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (gen != _flyGen || _menus.Count == 0) return;
            MenuEntry.Compact = true;
            var (title, build) = _menus.Peek();
            List<MenuEntry> entries;
            try { entries = build(); } catch { CloseMenus(); return; }

            var fly = new MenuFlyout();
            bool handled = false;
            fly.Items.Add(new MenuFlyoutItem { Text = title, IsEnabled = false });
            if (_menus.Count > 1)
            {
                var back = new MenuFlyoutItem { Text = "←  Tilbake" };
                back.Click += (_, _) => { handled = true; _sound.Play(Sfx.Back); _menus.Pop(); RebuildMenu(true); };
                fly.Items.Add(back);
            }
            fly.Items.Add(new MenuFlyoutSeparator());
            foreach (var entry in entries)
            {
                var m = entry;
                var item = new MenuFlyoutItem { Text = m.Title };
                if (m.Detail.Length > 0) item.KeyboardAcceleratorTextOverride = m.DetailText;
                if (m.OnAccept is null && m.OnAdjust is null) item.IsEnabled = false;
                else item.Click += async (_, _) =>
                {
                    handled = true;
                    _sound.Play(Sfx.Select);
                    int g = _flyGen;
                    try { if (m.OnAccept is not null) await m.OnAccept(); else m.OnAdjust!(1); } catch { }
                    if (_menus.Count == 0) return;
                    if (m.KeepOpen) ShowFlyoutMenu();       // verdi byttet: vis menyen på nytt
                    else if (g == _flyGen) CloseMenus();    // ingen ny meny åpnet: ferdig
                };
                fly.Items.Add(item);
            }
            // Lukket ved å klikke utenfor eller Esc: avslutt menymodus.
            fly.Closed += (_, _) => { if (!handled && ReferenceEquals(_flyout, fly)) CloseMenus(); };
            _flyout = fly;

            bool ok = _flyAnchor is { IsLoaded: true };
            FrameworkElement anchor = ok ? _flyAnchor! : DesktopLayer;
            Windows.Foundation.Point? pt = ok ? _flyPoint
                : new Windows.Foundation.Point(Math.Max(40, DesktopLayer.ActualWidth / 2 - 120), Math.Max(60, DesktopLayer.ActualHeight / 4));
            if (pt is { } p) fly.ShowAt(anchor, p); else fly.ShowAt(anchor);
        });
    }

    /// <summary>Menyen som flytende vindu midt på skjermen med kompakte rader i desktop-modus, og stor konsollmeny ellers.</summary>
    private void ApplyMenuStyle()
    {
        bool d = DesktopOn;
        MenuEntry.Compact = d;
        MenuPanel.HorizontalAlignment = d ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        MenuPanel.VerticalAlignment = d ? VerticalAlignment.Center : VerticalAlignment.Top;
        MenuPanel.Width = d ? 760 : 600;
        MenuPanel.MaxHeight = d ? 720 : 900;
        MenuPanel.Margin = d ? new Thickness(0) : new Thickness(64, 110, 0, 0);
        MenuPanel.CornerRadius = new CornerRadius(d ? 16 : 28);
        MenuPanel.Padding = d ? new Thickness(18, 18, 18, 14) : new Thickness(32, 32, 32, 24);
        MenuTitle.FontSize = d ? 20 : 30;
        MenuHint.Visibility = d ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DesktopMenu_Click(object sender, RoutedEventArgs e)
    {
        _flyAnchor = sender as FrameworkElement; _flyPoint = null;
        ToggleMenu();
    }

    private void DesktopAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        _sound.Play(Sfx.Open);
        OpenMenu("Legg til app", BuildAddMenu);
    }

    private void DesktopToConsole_Click(object sender, RoutedEventArgs e)
    {
        _settings.Data.DesktopMode = false;
        _settings.Save();
        ApplyWindowMode();
    }

    // ---------- Meny med mus ----------

    private void MenuOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Klikk utenfor panelet går ett steg tilbake.
        if (_mode == Mode.Menu) { e.Handled = true; Back(); }
    }

    private void MenuPanel_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void MenuBack_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Menu) return;
        Back();
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Menu) return;
        _sound.Play(Sfx.Back);
        CloseMenus();
    }

    /// <summary>◄ ► ved innstillinger som kan justeres (volum osv.), for de som ikke bruker kontroller.</summary>
    private void MenuAdjust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MenuEntry m } fe || m.OnAdjust is null) return;
        int dir = fe.Tag is string t && t == "-1" ? -1 : 1;
        MenuList.SelectedItem = m;
        m.OnAdjust(dir);
        RebuildMenu(false);
    }

    // ---------- Mus i konsollmodus ----------

    /// <summary>Knapp oppe til høyre i konsollvisningen: tilbake til Desktop-modus uten kontroller.</summary>
    private void ConsoleToDesktop_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        _settings.Data.DesktopMode = true;
        _settings.Save();
        ApplyWindowMode();
    }

    /// <summary>Klikk på en kategori øverst (Alle, Favoritter …).</summary>
    private void Chip_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_mode != Mode.Home || sender is not TextBlock tb) return;
        int i = _chips.IndexOf(tb);
        if (i < 0) return;
        e.Handled = true;
        _filter = i;
        _search = "";
        _sound.Play(Sfx.Move);
        RebuildTiles("");
    }

    /// <summary>Musehjulet blar sidelengs i spillraden.</summary>
    private void GameList_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_mode != Mode.Home || _tiles.Count == 0) return;
        int delta = e.GetCurrentPoint(GameList).Properties.MouseWheelDelta;
        if (delta == 0) return;
        int idx = Math.Clamp(GameList.SelectedIndex + (delta < 0 ? 1 : -1), 0, _tiles.Count - 1);
        GameList.SelectedIndex = idx;
        e.Handled = true;
    }

    /// <summary>Høyreklikk på en flis åpner spillmenyen (som View-knappen).</summary>
    private void GameList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TileVm t)
        {
            GameList.SelectedItem = t;
            e.Handled = true;
            OpenGameMenu();
        }
    }

    /// <summary>Ctrl+F: hopp til søkefeltet i desktop-modus.</summary>
    private bool TryFocusDesktopSearch(Windows.System.VirtualKey key)
    {
        if (!DesktopOn || key != Windows.System.VirtualKey.F) return false;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return false;
        DesktopSearch.Focus(FocusState.Keyboard);
        DesktopSearch.SelectAll();
        return true;
    }

    /// <summary>True når et tekstfelt har tastaturfokus (da skal rot-håndtereren ikke tolke tastene).</summary>
    private bool TypingInTextBox() =>
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox;
}
