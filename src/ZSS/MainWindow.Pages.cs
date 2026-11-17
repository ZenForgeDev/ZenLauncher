using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZenStationShell;

/// <summary>Desktop-sider for nedlastinger og samlinger (bruker samme sidevindu som innstillingene).</summary>
public sealed partial class MainWindow
{
    private const int PageDownloads = 6;
    private const int PageCollections = 7;

    private DispatcherTimer? _pageTimer;
    private string _dlSig = "";
    private readonly Dictionary<DlItem, (ProgressBar Bar, TextBlock Txt)> _dlCtl = new();
    private readonly HashSet<string> _colOpen = new();

    private void DesktopDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        ShowSettingsPage(PageDownloads);
    }

    private void DesktopCollections_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Home) return;
        ShowSettingsPage(PageCollections);
    }

    private void UpdateDlButton() =>
        DesktopDownloadsBtn.Content = _dlQueue.Count > 0 ? $"⬇ {_dlQueue.Count}" : "⬇";

    // ---------- Nedlastinger ----------

    private string DlSignature() => string.Join("|", _dlQueue.Select(d => d.Tile.Key + d.State)) + "#" + _dlDone.Count;

    private void StartPageTimer()
    {
        _pageTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pageTimer.Tick -= PageTick;
        _pageTimer.Tick += PageTick;
        _pageTimer.Start();
    }

    private void PageTick(object? sender, object e)
    {
        if (SettingsPage.Visibility != Visibility.Visible || _settingsPage != PageDownloads) { _pageTimer?.Stop(); return; }
        if (DlSignature() != _dlSig) { BuildSettingsBody(); return; }
        foreach (var kv in _dlCtl) UpdateDlControls(kv.Key, kv.Value.Bar, kv.Value.Txt);
    }

    private void UpdateDlControls(DlItem it, ProgressBar bar, TextBlock txt)
    {
        if (it.State == "active")
        {
            bar.IsIndeterminate = it.Percent < 0;
            if (it.Percent >= 0) bar.Value = it.Percent;
            txt.Text = it.Detail.Length > 0 ? it.Detail : "Laster ned…";
        }
        else txt.Text = $"I kø (nr. {_dlQueue.IndexOf(it)})";
    }

    private void BuildDownloadsPage()
    {
        _dlCtl.Clear();
        _dlSig = DlSignature();
        AddNote("Epic- og GOG-nedlastinger går via køen, én om gangen. Steam-nedlastinger vises i Steam og i banneret nederst.");
        if (_dlQueue.Count == 0) AddNote("Ingen pågående nedlastinger.");

        foreach (var d in _dlQueue.ToList())
        {
            var it = d;
            var card = new Grid { ColumnSpacing = 16, Padding = new Thickness(16) };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = it.Tile.Name, FontSize = 16, Foreground = Br(255, 255, 255, 255) });
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = it.State == "active" ? Visibility.Visible : Visibility.Collapsed };
            var txt = new TextBlock { FontSize = 12, Foreground = Br(0x99, 255, 255, 255) };
            left.Children.Add(bar);
            left.Children.Add(txt);
            card.Children.Add(left);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (it.State == "queued")
            {
                var first = new Button { Content = "Først i køen", CornerRadius = new CornerRadius(8) };
                first.Click += (_, _) =>
                {
                    if (_dlQueue.Remove(it))
                    {
                        var fq = _dlQueue.FindIndex(x => x.State == "queued");
                        _dlQueue.Insert(fq < 0 ? _dlQueue.Count : fq, it);
                    }
                    BuildSettingsBody();
                };
                btns.Children.Add(first);
            }
            var cancel = new Button { Content = "Avbryt", CornerRadius = new CornerRadius(8) };
            cancel.Click += (_, _) =>
            {
                if (it.State == "active") it.Cts?.Cancel(); else { _dlQueue.Remove(it); UpdateDlButton(); }
                BuildSettingsBody();
            };
            btns.Children.Add(cancel);
            Grid.SetColumn(btns, 1);
            card.Children.Add(btns);
            SettingsBody.Children.Add(new Border { Background = Br(0x14, 255, 255, 255), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 10), Child = card });
            _dlCtl[it] = (bar, txt);
            UpdateDlControls(it, bar, txt);
        }

        if (_dlDone.Count > 0)
        {
            SettingsBody.Children.Add(new TextBlock { Text = "Nylig fullført", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Br(0xCC, 255, 255, 255), Margin = new Thickness(0, 14, 0, 6) });
            foreach (var done in Enumerable.Reverse(_dlDone).Take(20))
                SettingsBody.Children.Add(new TextBlock { Text = done, FontSize = 13, Foreground = Br(0x99, 255, 255, 255), Margin = new Thickness(0, 2, 0, 2) });
        }
        StartPageTimer();
    }

    // ---------- Samlinger ----------

    private void AfterCollectionsChanged(bool resetFilter = false)
    {
        _settings.Save();
        if (resetFilter && _filter >= FBase) _filter = FAll;
        RebuildTiles();
        BuildSettingsBody();
    }

    private void BuildCollectionsPage()
    {
        var cols = _settings.Data.Collections;
        AddNote("Samlinger vises i filteret øverst i listen. Du kan også legge et spill i en samling fra høyreklikk-menyen (Samlinger…).");

        var tb = new TextBox { PlaceholderText = "Navn på ny samling", MinWidth = 220 };
        var add = new Button { Content = "Lag", CornerRadius = new CornerRadius(8) };
        void Make()
        {
            var n = tb.Text.Trim();
            if (n.Length == 0) return;
            if (cols.ContainsKey(n)) { ShowToast("Du har allerede en samling med det navnet"); return; }
            cols[n] = new List<string>();
            _colOpen.Add(n);
            AfterCollectionsChanged();
        }
        add.Click += (_, _) => Make();
        tb.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Make(); };
        var newRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        newRow.Children.Add(tb);
        newRow.Children.Add(add);
        AddRow("Ny samling", null, newRow);

        if (cols.Count == 0) { AddNote("Du har ingen samlinger ennå."); return; }

        var names = _gameTiles.Concat(_storeTiles).Concat(_settings.Data.Apps.Select(TileVm.FromApp))
            .GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Name);

        foreach (var cn in cols.Keys.ToList())
        {
            var name = cn;
            var keys = cols[name];
            var body = new StackPanel { Spacing = 4, Padding = new Thickness(4, 6, 4, 6) };

            var renameBox = new TextBox { Text = name, MinWidth = 200 };
            var rename = new Button { Content = "Gi nytt navn", CornerRadius = new CornerRadius(8) };
            rename.Click += (_, _) =>
            {
                var nn = renameBox.Text.Trim();
                if (nn.Length == 0 || nn == name) return;
                if (cols.ContainsKey(nn)) { ShowToast("Du har allerede en samling med det navnet"); return; }
                var copy = cols.ToList();
                cols.Clear();
                foreach (var kv in copy) cols[kv.Key == name ? nn : kv.Key] = kv.Value;
                if (_colOpen.Remove(name)) _colOpen.Add(nn);
                AfterCollectionsChanged();
            };
            var del = new Button { Content = "Slett samling", CornerRadius = new CornerRadius(8) };
            del.Click += (_, _) => { cols.Remove(name); _colOpen.Remove(name); AfterCollectionsChanged(true); };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            tools.Children.Add(renameBox);
            tools.Children.Add(rename);
            tools.Children.Add(del);
            body.Children.Add(tools);

            if (keys.Count == 0) body.Children.Add(new TextBlock { Text = "Tom. Legg spill i den fra høyreklikk-menyen på et spill.", FontSize = 12, Foreground = Br(0x99, 255, 255, 255) });
            foreach (var k in keys.ToList())
            {
                var key = k;
                var row = new Grid { Padding = new Thickness(4, 2, 4, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = names.TryGetValue(key, out var nm) ? nm : key, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Br(255, 255, 255, 255), TextTrimming = TextTrimming.CharacterEllipsis });
                var rm = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(6) };
                rm.Click += (_, _) => { keys.Remove(key); AfterCollectionsChanged(); };
                Grid.SetColumn(rm, 1);
                row.Children.Add(rm);
                body.Children.Add(row);
            }

            var ex = new Expander
            {
                Header = $"{name}   ·   {keys.Count} {(keys.Count == 1 ? "oppføring" : "oppføringer")}",
                IsExpanded = _colOpen.Contains(name),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Content = body,
            };
            ex.Expanding += (_, _) => _colOpen.Add(name);
            ex.Collapsed += (_, _) => _colOpen.Remove(name);
            SettingsBody.Children.Add(ex);
        }
    }
}
