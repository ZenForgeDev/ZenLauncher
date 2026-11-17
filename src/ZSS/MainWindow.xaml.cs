using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Devices.Radios;
using Windows.Gaming.Input;
using Windows.Storage.Pickers;
using ZenStationShell.Services;
using ZenTune.Core.Games;
using ZenTune.Core.Hardware;
using ZenTune.Core.Presets;

namespace ZenStationShell;

public sealed partial class MainWindow : Window
{
    private enum Mode { Home, Menu, Keyboard, Busy }
    private const int FAll = 0, FFav = 1, FRecent = 2, FApps = 3, FNotInst = 4, FHidden = 5, FFree = 6, FStream = 7, FBase = 8;
    private static readonly string[] SortNames = { "Standard", "A–Å", "Nylig installert", "Mest spilt", "Sist spilt" };
    private bool _naming;
    private string _name = "";
    private string? _nameTarget;
    // Flervalg: velg mange spill og bruk en handling på alle (skjul, samling, favoritt).
    private bool _multi;
    private readonly HashSet<string> _picked = new();
    private List<string>? _nameMulti;
    private string _namePurpose = ""; // "" = navn på samling, "epic" = innloggingskode
    private readonly List<TextBlock> _chips = new();

    private readonly ObservableCollection<TileVm> _tiles = new();
    private readonly List<TileVm> _gameTiles = new();
    private List<StoreGame> _storeGames = new();
    private readonly List<TileVm> _storeTiles = new();
    private readonly List<TileVm> _freeTiles = new();
    private readonly Dictionary<string, Microsoft.UI.Xaml.Media.ImageSource> _icons = new();
    private readonly SettingsStore _settings = SettingsStore.Load();
    private readonly UiSound _sound;
    private readonly BackgroundMusic _music;
    private readonly GamepadInput _pad;
    private readonly PadMouse _padMouse;
    private readonly GameLauncher _launcher = new();
    private readonly DispatcherQueueTimer _clock;
    private DispatcherQueueTimer? _idle;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _frameStory;
    private static readonly (string Name, string? Hex)[] FrameThemes =
    {
        ("Regnbue", null), ("Hvit", "#FFFFFFFF"), ("Blå", "#FF3B8CFF"), ("Rosa", "#FFFF3B7A"),
        ("Grønn", "#FF3BFF8A"), ("Oransje", "#FFFFB020"), ("Lilla", "#FF8A5CFF"), ("Av", ""),
    };
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _dimmed;
    private static readonly int[] DimSteps = { 0, 1, 2, 5, 10, 15 };
    private static readonly int[] SleepSteps = { 0, 15, 30, 60, 120 };
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly IntPtr _hwnd;
    private readonly Stack<(string Title, Func<List<MenuEntry>> Build)> _menus = new();

    private Mode _mode = Mode.Home;
    private int _filter = FAll;
    private string _search = "";
    private bool _ready;     // lyd på valg først etter første lasting
    private bool _quiet;     // undertrykk lyd ved programmatiske endringer
    private DateTime _lastAccept = DateTime.MinValue;
    private int _statusTick;
    private bool _scanning;
    private TileVm? _running; // spillet som kjører nå (hvis noe)
    private RadioAccessStatus? _btAccess;

    // Skjermtastatur
    private static readonly string[][] KbLayout =
    {
        new[] { "A","B","C","D","E","F","G","H","I","J" },
        new[] { "K","L","M","N","O","P","Q","R","S","T" },
        new[] { "U","V","W","X","Y","Z","Æ","Ø","Å","-" },
        new[] { "0","1","2","3","4","5","6","7","8","9","." },
        new[] { "Mellomrom","⌫","Ferdig","Lim inn" },
    };
    private Border[][] _kbKeys = Array.Empty<Border[]>();
    private int _kbRow, _kbCol;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "ZenLauncher " + AppVersion;
        HeaderName.Text = "ZenLauncher " + AppVersion;
        TitleBarName.Text = "ZenLauncher " + AppVersion;
        ApplyWindowMode();

        _sound = new UiSound(_settings);
        _music = new BackgroundMusic(_settings);
        _music.Apply();
        HeroImage.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(600) };
        HeroImage.SizeChanged += (_, e) => HeroImage.CenterPoint = new System.Numerics.Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        GameList.ItemsSource = _tiles;
        DesktopList.ItemsSource = _tiles;
        DesktopGrid.ItemsSource = _tiles;

        _pad = new GamepadInput(DispatcherQueue);
        _pad.Direction += OnDirection;
        _pad.Accept += () => _ = AcceptAsync();
        _pad.Back += () => Back();
        _pad.Menu += ToggleMenu;
        _pad.Favorite += () => { if (_mode == Mode.Keyboard) KbBackspace(); else ToggleFavorite(); };
        _pad.Search += OpenKeyboard;
        _pad.TabChange += CycleFilter;
        _pad.View += OpenGameMenu;

        _padMouse = new PadMouse(_settings.Data.PadMouse);
        _padMouse.Toggled += on => DispatcherQueue.TryEnqueue(() =>
        {
            _settings.Data.PadMouse = on;
            _settings.Save();
            ShowToast(on ? "Kontroller som mus: på" : "Kontroller som mus: av");
        });

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) =>
        {
            UpdateClock();
            _statusTick++;
            if (_statusTick % 15 == 1) _ = UpdateStatusAsync();
            // Stille ny skanning etter spill hvert minutt (bare på hjemskjermen).
            if (_statusTick % 60 == 30 && _mode == Mode.Home && _ready) _ = RescanAsync(false);
        };
        _clock.Start();
        UpdateClock();

        Activated += (_, e) => SetWindowActive(e.WindowActivationState != WindowActivationState.Deactivated);
        AppWindow.Changed += AppWindow_Changed;

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => ToastBox.Visibility = Visibility.Collapsed;

        Closed += (_, _) =>
        {
            _pad.Dispose();
            _padMouse.Dispose();
            _clock.Stop();
            _idle?.Stop();
            _music.Dispose();
        };

        BuildKeyboard();
        UpdateChips();
        UpdateIdleTimer();
        InitTray();
        Root.Loaded += async (_, _) =>
        {
            _ = UpdateStatusAsync();
            await LoadGamesAsync();
            _ = ApolloAutostartAsync(false);
            StartHostWatch();
            _ = StartupUpdateCheckAsync();
            _ = StartupToolsAsync();
        };
    }

    /// <summary>Hentes fram når Windows starter ZenLauncher på nytt (Game Bar «Home» osv.). Tilbake til hjemskjermen.</summary>
    public void BringToFront()
    {
        if (_mode == Mode.Menu) CloseMenus();
        else if (_mode == Mode.Keyboard) CloseKeyboard();

        try { AppWindow.Show(); } catch { }
        NativeMethods.ShowWindow(_hwnd, 9); // SW_RESTORE
        Activate();
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    /// <summary>Oppdaterer nettverk, Bluetooth og kontroller (batteri) ved siden av klokken.</summary>
    private async Task UpdateStatusAsync()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) { NetIcon.Glyph = "\uE701"; NetIcon.Opacity = 0.3; }
            else { NetIcon.Glyph = profile.IsWlanConnectionProfile ? "\uE701" : "\uE839"; NetIcon.Opacity = 1; }
        }
        catch { }

        // Kontroller: prosent vises bare for en trådløs kontroller med gyldig batterirapport.
        // Innebygd eller kablet kontroller (f.eks. på Ally X) viser ikonet uten prosent.
        try
        {
            var pads = Gamepad.Gamepads;
            PadStatus.Visibility = pads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            string padText = "";
            foreach (var pad in pads)
            {
                var rep = pad.TryGetBatteryReport();
                int? full = rep?.FullChargeCapacityInMilliwattHours;
                int? rem = rep?.RemainingCapacityInMilliwattHours;
                LogPadBattery(pad.IsWireless, rep?.Status, full, rem);
                if (padText.Length == 0 && pad.IsWireless && rep is not null &&
                    rep.Status != Windows.System.Power.BatteryStatus.NotPresent &&
                    full is int f && f > 0 && rem is int r && r >= 0)
                    padText = $"{Math.Clamp(r * 100 / f, 0, 100)} %";
            }
            PadBatteryText.Text = padText;
        }
        catch { }

        // Enhetens eget batteri (håndholdt/bærbar). Skjules når enheten ikke har batteri.
        try
        {
            var st = Windows.System.Power.PowerManager.BatteryStatus;
            if (st == Windows.System.Power.BatteryStatus.NotPresent)
                DeviceBattery.Visibility = Visibility.Collapsed;
            else
            {
                int pct = Math.Clamp(Windows.System.Power.PowerManager.RemainingChargePercent, 0, 100);
                DeviceBatteryIcon.Glyph = BatteryGlyph(pct, st == Windows.System.Power.BatteryStatus.Charging);
                DeviceBatteryText.Text = $"{pct} %";
                DeviceBattery.Visibility = Visibility.Visible;
            }
        }
        catch { DeviceBattery.Visibility = Visibility.Collapsed; }

        try
        {
            _btAccess ??= await Radio.RequestAccessAsync();
            if (_btAccess == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                BtIcon.Visibility = bt is null ? Visibility.Collapsed : Visibility.Visible;
                if (bt is not null) BtIcon.Opacity = bt.State == RadioState.On ? 1 : 0.3;
            }
            else BtIcon.Visibility = Visibility.Collapsed;
        }
        catch { BtIcon.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Segoe Fluent Icons: Battery0–9 = E850–E859, Battery10 = E83F, BatteryCharging0–8 = E85A–E862, 9 = E83E, 10 = EA93.</summary>
    private static string BatteryGlyph(int percent, bool charging)
    {
        int level = Math.Clamp((percent + 5) / 10, 0, 10);
        int code = charging
            ? level switch { 9 => 0xE83E, 10 => 0xEA93, _ => 0xE85A + level }
            : level == 10 ? 0xE83F : 0xE850 + level;
        return char.ConvertFromUtf32(code);
    }

    private string _lastPadSig = "";

    /// <summary>Skriver kontrollerens batteriverdier til battery.log (bare når de endrer seg), til feilsøking på Ally X.</summary>
    private void LogPadBattery(bool wireless, Windows.System.Power.BatteryStatus? status, int? full, int? rem)
    {
        try
        {
            var sig = $"IsWireless={wireless} Status={(status?.ToString() ?? "null")} Full={full?.ToString() ?? "null"} Remaining={rem?.ToString() ?? "null"}";
            if (sig == _lastPadSig) return;
            _lastPadSig = sig;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "battery.log");
            if (File.Exists(file) && new FileInfo(file).Length > 50_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  kontroller: {sig}{Environment.NewLine}");
        }
        catch { }
    }

    private bool _isActive = true;

    /// <summary>
    /// Når ZenLauncher ikke har fokus (et spill eller en annen app er i forgrunnen) pauser vi alt som koster CPU:
    /// kontrolleravlesing, klokke/statusoppdatering og den roterende regnbueanimasjonen (hjemskjermen skjules).
    /// </summary>
    private void SetWindowActive(bool active)
    {
        if (_isActive == active) return;
        _isActive = active;

        _pad.SetActive(active);
        _music.SetActive(active);
        UpdateIdleTimer();
        if (active) ApplyFrameTheme(); else { _frameStory?.Stop(); _frameStory = null; }
        ApplyLayerVisibility();

        if (active)
        {
            UpdateClock();
            _clock.Start();
            _ = UpdateStatusAsync();
        }
        else
        {
            _clock.Stop();
        }
    }

    /// <summary>Registrerer aktivitet i ZenLauncher (kontroller/tastatur) og tar bort dimming.</summary>
    private void Touch()
    {
        _lastActivity = DateTime.UtcNow;
        if (_dimmed) SetDim(false);
    }

    private void SetDim(bool on)
    {
        _dimmed = on;
        DimOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Tidtakeren går bare når dimming/auto-hvilemodus er slått på og ZenLauncher er synlig.</summary>
    private void UpdateIdleTimer()
    {
        var d = _settings.Data;
        bool need = _isActive && (d.DimMinutes > 0 || d.SleepMinutes > 0);
        if (!need)
        {
            _idle?.Stop();
            if (_dimmed) SetDim(false);
            return;
        }
        if (_idle is null)
        {
            _idle = DispatcherQueue.CreateTimer();
            _idle.Interval = TimeSpan.FromSeconds(5);
            _idle.Tick += (_, _) => IdleTick();
        }
        _lastActivity = DateTime.UtcNow;
        _idle.Start();
    }

    private void IdleTick()
    {
        var d = _settings.Data;
        double idle = Math.Min((DateTime.UtcNow - _lastActivity).TotalSeconds, NativeMethods.SystemIdleSeconds());
        if (d.DimMinutes > 0 && idle >= d.DimMinutes * 60)
        {
            if (!_dimmed) SetDim(true);
        }
        else if (_dimmed) SetDim(false);

        if (d.SleepMinutes > 0 && idle >= d.SleepMinutes * 60)
        {
            _lastActivity = DateTime.UtcNow; // unngå ny hvilemodus rett etter oppvåkning
            SetDim(false);
            NativeMethods.SetSuspendState(false, false, false);
        }
    }

    /// <summary>Flytter heltebildet litt sidelengs etter valgt posisjon. Går på GPU via implisitt animasjon.</summary>
    /// <summary>Konsollmodus = fullskjerm. Desktop-modus = vanlig, maksimert vindu med tittellinje.</summary>
    private void ApplyWindowMode()
    {
        try
        {
            if (_settings.Data.DesktopMode)
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                if (AppWindow.Presenter is OverlappedPresenter op) op.Maximize();
            }
            else
            {
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
        }
        catch { }
        ApplyTitleBar();
        ApplyLayerVisibility();
    }

    /// <summary>Desktop-modus bruker en egen tittellinje (tittel «ZenLauncher», dra for å flytte); konsollmodus har ingen.</summary>
    private void ApplyTitleBar()
    {
        try
        {
            bool d = _settings.Data.DesktopMode;
            // Innholdet strekkes alltid inn i tittellinjen, slik at systemet aldri maler en hvit stripe øverst.
            ExtendsContentIntoTitleBar = true;
            var tb = AppWindow.TitleBar;
            var dark = Windows.UI.Color.FromArgb(255, 5, 6, 10);
            var clear = Microsoft.UI.Colors.Transparent;
            tb.BackgroundColor = dark;
            tb.InactiveBackgroundColor = dark;
            tb.ButtonBackgroundColor = clear;
            tb.ButtonInactiveBackgroundColor = clear;
            tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
            tb.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(70, 255, 255, 255);
            tb.ButtonForegroundColor = Microsoft.UI.Colors.White;
            tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 140, 140, 150);
            if (d)
            {
                tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
                SetTitleBar(DesktopTitleBar);
            }
            else
            {
                // Konsollmodus: ingen synlig tittellinje eller knapper.
                tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
            }
        }
        catch { }
    }

    private bool _fixingPresenter;

    /// <summary>
    /// Xbox mode (og Windows ellers) kan sette vinduet tilbake til vanlig vindu. I konsollmodus tvinger vi
    /// fullskjerm tilbake med en gang, slik at tittellinjeknappene ikke blir stående.
    /// </summary>
    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs e)
    {
        if (_settings.Data.DesktopMode || _fixingPresenter) return;
        if (!(e.DidPresenterChange || e.DidSizeChange)) return;
        if (sender.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;
        _fixingPresenter = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen); ApplyTitleBar(); }
            catch { }
            finally { _fixingPresenter = false; }
        });
    }

    private void ApplyParallax()
    {
        if (!_settings.Data.Parallax)
        {
            HeroImage.Scale = System.Numerics.Vector3.One;
            HeroImage.Translation = System.Numerics.Vector3.Zero;
            return;
        }
        float x = 30 - Math.Min(GameList.SelectedIndex * 6, 60);
        HeroImage.Scale = new System.Numerics.Vector3(1.08f, 1.08f, 1f);
        HeroImage.Translation = new System.Numerics.Vector3(x, 0, 0);
    }

    private void StepFrame(int dir)
    {
        var d = _settings.Data;
        d.FrameTheme = (d.FrameTheme + dir + FrameThemes.Length) % FrameThemes.Length;
        _settings.Save();
        ApplyFrameTheme();
    }

    private static string MinText(int m) => m == 0 ? "Av" : m >= 60 ? $"{m / 60} t" : $"{m} min";

    private void StepSetting(int dir, int[] steps, int current, Action<int> set)
    {
        int i = Array.IndexOf(steps, current);
        if (i < 0) i = 0;
        set(steps[(i + dir + steps.Length) % steps.Length]);
        _settings.Save();
        UpdateIdleTimer();
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T fe && fe.Name == name) return fe;
            var r = FindNamed<T>(c, name);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>Setter rammen på valgt flis etter valgt tema. Bare den valgte flisen animeres, og bare i regnbue-modus.</summary>
    private void ApplyFrameTheme(int attempt = 0)
    {
        _frameStory?.Stop();
        _frameStory = null;
        if (!_isActive || GameList.SelectedItem is null) return;

        var cont = GameList.ContainerFromItem(GameList.SelectedItem) as ListViewItem;
        var hl = cont is null ? null : FindNamed<Border>(cont, "Highlight");
        if (hl is null)
        {
            if (attempt < 3) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ApplyFrameTheme(attempt + 1));
            return;
        }

        var theme = FrameThemes[Math.Clamp(_settings.Data.FrameTheme, 0, FrameThemes.Length - 1)];
        if (theme.Hex == "") { hl.Visibility = Visibility.Collapsed; return; }
        hl.Visibility = Visibility.Visible;

        if (theme.Hex is not null)
        {
            var c = Windows.UI.Color.FromArgb(
                Convert.ToByte(theme.Hex.Substring(1, 2), 16), Convert.ToByte(theme.Hex.Substring(3, 2), 16),
                Convert.ToByte(theme.Hex.Substring(5, 2), 16), Convert.ToByte(theme.Hex.Substring(7, 2), 16));
            hl.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);
            return;
        }

        var rot = new Microsoft.UI.Xaml.Media.RotateTransform { CenterX = 0.5, CenterY = 0.5 };
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            RelativeTransform = rot,
        };
        string[] cols = { "#FFFF3B7A", "#FFFFB020", "#FF3BFF8A", "#FF20C8FF", "#FF8A5CFF", "#FFFF3B7A" };
        for (int i = 0; i < cols.Length; i++)
        {
            var h = cols[i];
            brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
            {
                Color = Windows.UI.Color.FromArgb(255, Convert.ToByte(h.Substring(3, 2), 16), Convert.ToByte(h.Substring(5, 2), 16), Convert.ToByte(h.Substring(7, 2), 16)),
                Offset = i * 0.2,
            });
        }
        hl.BorderBrush = brush;

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 360, Duration = new Duration(TimeSpan.FromSeconds(3.5)),
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, rot);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Angle");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
        _frameStory = sb;
    }

    private void UpdateClock()
    {
        var text = DateTime.Now.ToString("HH:mm");
        if (ClockText.Text != text) ClockText.Text = text;
    }

    // ---------- Lasting og fliser ----------

    private async Task LoadGamesAsync()
    {
        var entries = await Task.Run(ZenStationShell.Services.SteamLibrary.Scan);
        var vms = entries.Select(TileVm.FromGame).ToList();
        await Task.WhenAll(vms.Select(v => v.LoadImagesAsync()));
        _gameTiles.AddRange(vms);

        foreach (var a in _settings.Data.Apps) await LoadIconAsync(a);
        await RefreshStoreGamesAsync();

        LoadFreeTiles();
        RebuildTiles("");
        _ready = true;
        _ = RefreshLibrariesInBackgroundAsync();
        _ = ScanInstalledAppsAsync();
    }

    private static bool CacheStale(string path) =>
        !File.Exists(path) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalHours > 12;

    /// <summary>Holder Epic- og GOG-bibliotekene ferske uten at du må gjøre noe: hentes i bakgrunnen hvis de er eldre enn 12 timer.</summary>
    private async Task RefreshLibrariesInBackgroundAsync()
    {
        try
        {
            bool changed = false;
            if (LegendaryService.IsAvailable && LegendaryService.IsLoggedIn && CacheStale(LegendaryService.LibraryCachePath))
                changed |= (await LegendaryService.ListLibraryAsync()).Count > 0;
            if (GogService.IsAvailable && GogService.HasToken && CacheStale(GogService.LibraryCachePath))
                changed |= (await GogService.ListLibraryAsync()).Count > 0;
            if (_settings.Data.SteamApiKey.Length > 0 && SteamOwned.CacheStale())
                changed |= (await SteamOwned.ListLibraryAsync(_settings.Data.SteamApiKey)).Count > 0;
            if (changed) await RescanAsync(false);
            if (FreeGames.CacheStale())
            {
                var free = await FreeGames.RefreshAsync();
                if (free.Count > 0) { LoadFreeTiles(); if (_filter == FFree) RebuildTiles(); }
            }
        }
        catch { }
    }

    /// <summary>Bygger flisene til Gratis-fanen fra bufferen. Siste flis er den påkrevde lenken til GamerPower.com.</summary>
    private void LoadFreeTiles()
    {
        _freeTiles.Clear();
        foreach (var g in FreeGames.LoadCache()) _freeTiles.Add(TileVm.FromFree(g));
        _freeTiles.Add(TileVm.FromFree(new FreeGame("gamerpower", "Gratis-tilbud fra GamerPower.com", "", FreeGames.AttributionUrl, null, "")));
    }

    /// <summary>
    /// Skanner Steam på nytt. Legger bare til spill vi ikke allerede har (på AppId), oppdaterer sist spilt,
    /// og fjerner spill som er avinstallert. announce: si fra også når det ikke er noe nytt.
    /// </summary>
    private async Task RescanAsync(bool announce)
    {
        // Pågår en skanning (f.eks. den automatiske) venter vi i stedet for å hoppe over, slik at et nettopp installert spill alltid oppdateres.
        for (int i = 0; i < 60 && _scanning; i++) await Task.Delay(500);
        if (_scanning) return;
        _scanning = true;
        try
        {
            var entries = await Task.Run(ZenStationShell.Services.SteamLibrary.Scan);
            int storeBefore = _storeGames.Count(g => !OtherStores.IsInstallPlaceholder(g.Target));
            bool storesChanged = await RefreshStoreGamesAsync();
            int storeAdded = Math.Max(0, _storeGames.Count(g => !OtherStores.IsInstallPlaceholder(g.Target)) - storeBefore);
            if (entries.Count == 0)
            {
                if (storesChanged) RebuildTiles();
                if (announce) ShowToast(storeAdded > 0 ? (storeAdded == 1 ? "Nytt spill funnet" : $"{storeAdded} nye spill funnet")
                    : _storeGames.Count > 0 ? "Fant ingen Steam-spill." : "Fant ingen spill.");
                return;
            }

            var existing = _gameTiles.Where(t => t.Game is not null).ToDictionary(t => t.Game!.AppId);
            var result = new List<TileVm>();
            var added = new List<TileVm>();
            foreach (var e in entries)
            {
                if (existing.TryGetValue(e.AppId, out var tile))
                {
                    tile.UpdateGame(e);
                    result.Add(tile);
                }
                else
                {
                    var n = TileVm.FromGame(e);
                    await n.LoadImagesAsync();
                    result.Add(n);
                    added.Add(n);
                }
            }

            bool removed = result.Count - added.Count < _gameTiles.Count;
            _gameTiles.Clear();
            _gameTiles.AddRange(result);

            if (added.Count > 0 || removed || storesChanged) RebuildTiles();

            int newTotal = added.Count + storeAdded;
            if (added.Count == 1 && storeAdded == 0)
                ShowToast($"Nytt spill: {added[0].Name}");
            else if (newTotal > 0)
                ShowToast(newTotal == 1 ? "Nytt spill funnet" : $"{newTotal} nye spill funnet");
            else if (announce)
                ShowToast("Ingen nye spill funnet.");
        }
        catch (Exception ex)
        {
            if (announce) ShowToast("Skanning feilet: " + ex.Message);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>Skanner Epic, GOG og Ubisoft. Returnerer true hvis listen er endret.</summary>
    private async Task<bool> RefreshStoreGamesAsync()
    {
        var found = await Task.Run(OtherStores.Scan);
        // Nøkkel + mål: et spill som går fra «ikke installert» til installert beholder nøkkelen, men får nytt mål.
        if (found.Select(g => g.Key + "|" + g.Target).SequenceEqual(_storeGames.Select(g => g.Key + "|" + g.Target))) return false;

        _storeGames = found;
        _storeTiles.Clear();
        foreach (var g in found)
        {
            _storeTiles.Add(TileVm.FromStore(g));
            if (g.IconExe is not null && !_icons.ContainsKey(g.Key))
            {
                var icon = await ImageLoader.LoadAppIconAsync(new AppEntry(g.Name, g.IconExe));
                if (icon is not null) _icons[g.Key] = icon;
            }
        }
        return true;
    }

    private async Task LoadIconAsync(AppEntry a)
    {
        var key = $"app:{a.Target}";
        if (_icons.ContainsKey(key)) return;
        var icon = await ImageLoader.LoadAppIconAsync(a);
        if (icon is not null) _icons[key] = icon;
    }

    /// <summary>Bygger flisene på nytt ut fra kategori og søk. selectKey: flis som skal velges ("" = første).</summary>
    private void RebuildTiles(string? selectKey = null)
    {
        selectKey ??= (GameList.SelectedItem as TileVm)?.Key;

        var apps = _settings.Data.Apps.Select(TileVm.FromApp).ToList();
        var all = _gameTiles.Concat(_storeTiles).Concat(apps).ToList();
        var cont = ContinueTile();
        foreach (var t in all)
        {
            t.Star = _settings.IsFavorite(t.Key) ? "★" : "";
            t.Badge = _multi && _picked.Contains(t.Key) ? "✓ VALGT"
                : ReferenceEquals(t, cont) ? "FORTSETT" : t.NotInstalled ? "IKKE INSTALLERT" : "";
            if (t.Kind == TileKind.App && _icons.TryGetValue(t.Key, out var ic))
            {
                t.Icon = ic;
                t.Glyph = "";
            }
        }

        all = SortTiles(all);
        var colNames = _settings.Data.Collections.Keys.ToList();
        if (_filter >= FBase + colNames.Count) _filter = FAll;
        if (_filter == FHidden && !_settings.Data.ShowHiddenTab) _filter = FAll;
        if (_filter == FStream && _search.Length == 0) RefreshStreamTiles(false);

        // Skjulte spill filtreres bort her, ett sted, slik at de ikke dukker opp i noen annen visning.
        var hiddenKeys = _settings.Data.Hidden.ToHashSet();
        var visible = all.Where(t => !hiddenKeys.Contains(t.Key)).ToList();

        List<TileVm> list;
        if (_search.Length > 0)
        {
            list = visible.Where(t => t.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            list = _filter switch
            {
                FFav => visible.Where(t => t.Star.Length > 0).ToList(),
                >= FBase => CollectionTiles(colNames[_filter - FBase], visible),
                FRecent => _gameTiles.Concat(_storeTiles).Where(t => !t.NotInstalled && !hiddenKeys.Contains(t.Key) && LastPlayedOf(t) > 0).OrderByDescending(LastPlayedOf).Take(12).ToList(),
                FApps => apps.Where(a => !hiddenKeys.Contains(a.Key)).Concat(new[] { TileVm.AddTile() }).ToList(),
                FNotInst => visible.Where(t => t.NotInstalled).ToList(),
                FHidden => all.Where(t => hiddenKeys.Contains(t.Key)).ToList(),
                FFree => _freeTiles.ToList(),
                FStream => StreamTiles(),
                _ => (cont is null ? visible.Where(t => !t.NotInstalled).ToList() : new[] { cont }.Concat(visible.Where(t => !ReferenceEquals(t, cont) && !t.NotInstalled)).ToList())
                        .Concat(new[] { TileVm.AddTile() }).ToList(),
            };
        }

        _quiet = true;
        _tiles.Clear();
        foreach (var t in list) _tiles.Add(t);
        if (list.Count > 0)
        {
            int idx = string.IsNullOrEmpty(selectKey) ? 0 : Math.Max(0, list.FindIndex(t => t.Key == selectKey));
            GameList.SelectedIndex = idx;
        }
        _quiet = false;

        if (list.Count == 0)
        {
            EmptyText.Text = _search.Length > 0 ? $"Ingen treff for «{_search}»."
                : _filter == FFav ? "Ingen favoritter ennå. Trykk Y (F2 på tastatur) på et spill eller en app."
                : _filter == FRecent ? "Ingen spilte spill ennå."
                : _filter == FNotInst ? "Ingen uinstallerte Epic-spill. Logg inn under Meny → Kontoer… og hent biblioteket."
                : _filter == FHidden ? "Ingen skjulte spill. Åpne menyen på et spill og velg Skjul."
                : _filter == FFree ? "Ingen gratisspill akkurat nå. Listen oppdateres automatisk."
                : _filter == FStream ? (_streamRefreshing ? "Henter spill fra verter…"
                    : _settings.Data.StreamHosts.Count == 0 ? "Ingen verter ennå. Legg til og par en vert under Meny → Streaming…"
                    : _streamErr.Length > 0 ? "Fant ingen spill. " + _streamErr
                    : "Ingen spill å strømme. Sørg for at verts-PC-en er på og at dere er paret.")
                : _filter >= FBase ? "Samlingen er tom. Åpne menyen på et spill og velg Samlinger…"
                : "Fant ingen installerte Steam-spill. Sjekk at Steam er installert og at du har logget inn minst én gang.";
        }
        EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SearchText.Visibility = _search.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchText.Text = "Søk: " + _search + "▏";
        if (!_naming) KbText.Text = _search + "▏";

        UpdateChips();
        if (DesktopOn) SyncDesktopFilter();
        else if (_mode == Mode.Home) GameList.Focus(FocusState.Programmatic);
    }

    private void UpdateChips()
    {
        var names = new List<string> { "Alle", "★ Favoritter", "Sist spilt", "Apper", "Ikke installert", "Skjult", "Gratis", "Streaming" };
        names.AddRange(_settings.Data.Collections.Keys);
        if (_chips.Count != names.Count || _chips.Where((c, i) => c.Text != names[i]).Any())
        {
            ChipsPanel.Children.Clear();
            _chips.Clear();
            foreach (var n in names)
            {
                var tb = new TextBlock { Text = n, FontSize = 22, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) };
                tb.Tapped += Chip_Tapped;
                _chips.Add(tb);
                ChipsPanel.Children.Add(tb);
            }
        }
        for (int i = 0; i < _chips.Count; i++)
        {
            bool on = _search.Length == 0 && i == _filter;
            _chips[i].Visibility = i == FHidden && !_settings.Data.ShowHiddenTab ? Visibility.Collapsed : Visibility.Visible;
            _chips[i].Opacity = on ? 1.0 : 0.45;
            _chips[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        // Smal skjerm (Ally X): mindre skrift og tettere mellomrom, og raden ruller slik at valgt fane alltid er synlig.
        double w = (Content as FrameworkElement)?.ActualWidth ?? 0;
        double fs = w > 0 && w < 1500 ? 18 : 22;
        ChipsPanel.Spacing = fs < 22 ? 22 : 34;
        foreach (var c in _chips) c.FontSize = fs;
        int cur = _search.Length == 0 ? _filter : -1;
        if (cur >= 0 && cur < _chips.Count)
            _chips[cur].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, HorizontalAlignmentRatio = 0.5 });
    }

    private void ChipsScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateChips();

    private List<TileVm> SortTiles(List<TileVm> all)
    {
        var play = _settings.Data.PlaySeconds;
        return _settings.Data.SortMode switch
        {
            1 => all.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            2 => all.OrderByDescending(t => t.Installed).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            3 => all.OrderByDescending(t => play.TryGetValue(t.Key, out var s) ? s : 0).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            4 => all.OrderByDescending(t => LastPlayedOf(t)).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => all,
        };
    }

    private List<TileVm> CollectionTiles(string name, List<TileVm> all)
    {
        var keys = _settings.Data.Collections.TryGetValue(name, out var k) ? k : new List<string>();
        return all.Where(t => keys.Contains(t.Key)).ToList();
    }

    private TileVm? _heroTile;

    /// <summary>Viser heltebildet til valgt flis. Bildet lastes først nå, og forrige flis' bilde slippes (sparer mye RAM).</summary>
    private async Task ShowHeroAsync(TileVm t)
    {
        var previous = _heroTile;
        _heroTile = t;
        try
        {
            if (t.Hero is null) await t.EnsureHeroAsync();
            if (!ReferenceEquals(_heroTile, t)) return; // utvalget har flyttet seg videre
            HeroImage.Source = t.Hero;
            if (DesktopOn) DesktopHero.Source = t.Hero;
        }
        catch { }
        if (previous is not null && !ReferenceEquals(previous, t)) previous.ReleaseHero();
    }

    // ---------- Fortsett og spilletid ----------

    private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private long LastPlayedOf(TileVm t)
    {
        long mine = _settings.Data.LastLaunch.TryGetValue(t.Key, out var l) ? l : 0;
        return Math.Max(mine, t.Game?.LastPlayed ?? 0);
    }

    /// <summary>Spillet du sist spilte (Steam eller Epic/GOG/Ubisoft). Apper og nettsider teller ikke.</summary>
    private TileVm? ContinueTile() =>
        _gameTiles.Concat(_storeTiles)
            .Where(t => !_settings.Data.Hidden.Contains(t.Key))
            .Select(t => (t, ts: LastPlayedOf(t)))
            .Where(x => x.ts > 0)
            .OrderByDescending(x => x.ts)
            .Select(x => x.t)
            .FirstOrDefault();

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return "";
        long min = seconds / 60;
        return min >= 60 ? $"{min / 60} t {min % 60} min" : $"{min} min";
    }

    private static string LastPlayedText(long ts)
    {
        int days = (DateTime.Now.Date - DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.Date).Days;
        return days <= 0 ? "i dag" : days == 1 ? "i går" : days < 7 ? $"{days} dager siden"
            : DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("dd.MM.yyyy");
    }

    private string MetaFor(TileVm t)
    {
        if (t.Kind == TileKind.Add) return "";
        var parts = new List<string>();

        long ts = LastPlayedOf(t);
        if (ts > 0) parts.Add("Sist spilt " + LastPlayedText(ts));

        if (_settings.Data.PlaySeconds.TryGetValue(t.Key, out var secs) && FormatDuration(secs) is { Length: > 0 } dur)
            parts.Add("Spilletid " + dur);

        return string.Join("  ·  ", parts);
    }

    private void RefreshMeta()
    {
        if (GameList.SelectedItem is TileVm t) MetaText.Text = MetaFor(t);
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is not TileVm t)
        {
            HeroImage.Source = null;
            TitleText.Text = "";
            MetaText.Text = "";
            HintText.Text = "";
            SyncDesktopDetail(null);
            return;
        }

        _ = ShowHeroAsync(t);
        if (DesktopOn)
        {
            // Desktop: ingen konsollanimasjon, ingen skjult rulling og ingen lyd ved museklikk (det ga hakking).
            SyncDesktopDetail(t);
            return;
        }
        ApplyParallax();
        ApplyFrameTheme();
        TitleText.Text = t.Name;
        MetaText.Text = MetaFor(t);
        HintText.Text = HintFor(t);
        GameList.ScrollIntoView(t);
        SyncDesktopDetail(t);

        if (_ready && !_quiet && _mode == Mode.Home) _sound.Play(Sfx.Move);
    }

    private void GameList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TileVm t)
        {
            GameList.SelectedItem = t;
            _ = AcceptAsync();
        }
    }

    private void CycleFilter(int dir)
    {
        if (_mode == Mode.Menu && MenuTabs(dir)) return;
        if (_mode != Mode.Home) return;
        _search = "";
        int n = FBase + _settings.Data.Collections.Count;
        do { _filter = (_filter + dir + n) % n; } while (_filter == FHidden && !_settings.Data.ShowHiddenTab);
        _sound.Play(Sfx.Move);
        RebuildTiles("");
    }

    private void ToggleFavorite()
    {
        if (_mode != Mode.Home || GameList.SelectedItem is not TileVm t || t.Kind == TileKind.Add) return;
        bool now = _settings.ToggleFavorite(t.Key);
        _sound.Play(Sfx.Select);
        ShowToast(now ? $"★ {t.Name} er favoritt" : $"{t.Name} er ikke lenger favoritt");
        if (DesktopOn && _filter != FFav && _search.Length == 0)
        {
            t.Star = now ? "★" : ""; // oppdaterer stjernen uten å bygge hele listen på nytt (ingen flimring/rulling)
            return;
        }
        RebuildTiles(t.Key);
    }

    // ---------- Spillmeny (View-knappen / F3) ----------

    /// <summary>View åpner menyen for valgt spill. View igjen lukker en åpen meny.</summary>
    private void OpenGameMenu()
    {
        Touch();
        if (_mode == Mode.Menu) { _sound.Play(Sfx.Back); CloseMenus(); return; }
        if (_mode == Mode.Home && _multi)
        {
            _sound.Play(Sfx.Open);
            OpenMenu($"{_picked.Count} valgt", BuildMultiMenu);
            return;
        }
        if (_mode != Mode.Home || GameList.SelectedItem is not TileVm { Kind: not TileKind.Add } t) return;
        _sound.Play(Sfx.Open);
        OpenMenu(t.Name, () => BuildGameMenu(t));
    }

    private List<MenuEntry> BuildGameMenu(TileVm t)
    {
        var d = _settings.Data;
        var list = new List<MenuEntry>();
        bool running = _running is not null && _running.Key == t.Key;

        // Starte
        if (t.NotInstalled)
            list.Add(new("Installer", t.StoreName)
            {
                OnAccept = () => { CloseMenus(); LaunchApp(t); return Task.CompletedTask; },
            });
        else
            list.Add(new(running ? "Gå tilbake til spillet" : t.Kind == TileKind.Game || t.Store is not null ? "Spill" : "Åpne", t.StoreName)
            {
                OnAccept = async () =>
                {
                    CloseMenus();
                    if (t.Kind == TileKind.Game) await LaunchGameAsync(t); else LaunchApp(t);
                },
            });

        if (!DesktopOn && t.Kind != TileKind.Add)
        {
            list.Add(new("Detaljer…", "beskrivelse, tid å fullføre, spilletid, størrelse")
            { OnAccept = () => { _ = OpenDetailsAsync(t); return Task.CompletedTask; } });
            if (PluginOn(Plugins.LocalShots.Id) && t.Game is { } shotGame)
            {
                var shotFiles = Plugins.LocalShots.Find(shotGame.AppId);
                if (shotFiles.Count > 0)
                    list.Add(new($"Egne skjermbilder ({shotFiles.Count})", "bla med ◄ ►")
                    { OnAccept = () => { CloseMenus(); OpenShotViewer(shotFiles); return Task.CompletedTask; } });
            }
        }

        // Ordne
        list.Add(new("Favoritt", _settings.IsFavorite(t.Key) ? "★ På" : "Av")
        {
            KeepOpen = true,
            OnAccept = () => { _settings.ToggleFavorite(t.Key); RebuildTiles(t.Key); return Task.CompletedTask; },
        });
        int cols = d.Collections.Count(c => c.Value.Contains(t.Key));
        list.Add(new("Samlinger…", cols > 0 ? $"i {cols} samling(er)" : "legg i Co-op, Skal spille, …")
        {
            OnAccept = () => { OpenMenu("Samlinger", () => BuildCollectionsMenu(t)); return Task.CompletedTask; },
        });
        list.Add(new("Velg flere", "merk flere spill, så skjuler eller samler du dem samtidig")
        {
            OnAccept = () => { CloseMenus(); StartMulti(t); return Task.CompletedTask; },
        });
        bool hidden = d.Hidden.Contains(t.Key);
        list.Add(new(hidden ? "Vis igjen" : "Skjul", hidden ? "flytt tilbake til biblioteket" : "flytt til Skjult-fanen")
        {
            OnAccept = () => { CloseMenus(); ToggleHidden(t); return Task.CompletedTask; },
        });

        // Lyd
        if (!t.NotInstalled)
        {
            int vol = d.GameVolume.TryGetValue(t.Key, out var gv) ? gv : 100;
            list.Add(new("Spillvolum", $"◄ {vol} % ►   (A: 100 %)")
            {
                KeepOpen = true,
                OnAccept = () => { SetGameVolume(t, 100); return Task.CompletedTask; },
                OnAdjust = step => SetGameVolume(t, (d.GameVolume.TryGetValue(t.Key, out var cur) ? cur : 100) + step * 5),
            });
        }

        // Om spillet
        var gameDir = t.Game?.InstallPath ?? t.InstallDir;
        if (!t.NotInstalled && !string.IsNullOrWhiteSpace(gameDir) && Directory.Exists(gameDir))
            list.Add(new("Åpne installasjonsmappe", gameDir)
            {
                OnAccept = () => { CloseMenus(); OpenExternal(gameDir); return Task.CompletedTask; },
            });
        list.Add(new("Egenskaper…", "spilletid, sist spilt, mappe")
        {
            OnAccept = () => { OpenMenu("Egenskaper", () => BuildPropertiesMenu(t)); return Task.CompletedTask; },
        });

        // Nederst: fjerne
        if (t.Kind == TileKind.App && t.Store is null)
            list.Add(new("Fjern fra listen", "filene slettes ikke")
            {
                OnAccept = () =>
                {
                    d.Apps.RemoveAll(a => a.Target == t.App!.Target);
                    d.Favorites.Remove(t.Key);
                    _settings.Save();
                    CloseMenus();
                    RebuildTiles("");
                    ShowToast($"{t.Name} er fjernet");
                    return Task.CompletedTask;
                },
            });
        else if (!t.NotInstalled && t.Kind == TileKind.Game && t.Game is { } sg)
            list.Add(new("Avinstaller…", "åpner Steams egen bekreftelse")
            {
                OnAccept = () =>
                {
                    OpenMenu($"Avinstaller {t.Name}?", () => BuildConfirmMenu("Steam spør deg én gang til", () =>
                    {
                        try { Process.Start(new ProcessStartInfo($"steam://uninstall/{sg.AppId}") { UseShellExecute = true }); ShowToast("Bekreft avinstallering i Steam"); }
                        catch (Exception ex) { ShowToast("Kunne ikke åpne Steam: " + ex.Message); }
                        return Task.CompletedTask;
                    }));
                    return Task.CompletedTask;
                },
            });
        else if (!t.NotInstalled && t.Store == "Epic" && t.Key.StartsWith("epic:", StringComparison.Ordinal) && OtherStores.IsLegendaryInstalled(t.Key[5..]))
            list.Add(new("Avinstaller…", "sletter spillet fra PC-en")
            {
                OnAccept = () =>
                {
                    OpenMenu($"Avinstaller {t.Name}?", () => BuildConfirmMenu("spillfilene slettes", () => UninstallEpicAsync(t)));
                    return Task.CompletedTask;
                },
            });

        else if (!t.NotInstalled && t.Store == "GOG" && t.Key.StartsWith("gog:", StringComparison.Ordinal) && GogService.IsInstalledByUs(t.Key[4..]))
            list.Add(new("Avinstaller…", "sletter spillmappen fra PC-en")
            {
                OnAccept = () =>
                {
                    OpenMenu($"Avinstaller {t.Name}?", () => BuildConfirmMenu("spillmappen slettes", () => UninstallGogAsync(t)));
                    return Task.CompletedTask;
                },
            });

        return list;
    }

    /// <summary>Bekreftelse før noe som sletter. «Nei» er valgt fra start.</summary>
    private List<MenuEntry> BuildConfirmMenu(string consequence, Func<Task> action) => new()
    {
        new("Nei, behold", "") { OnAccept = () => { Back(); return Task.CompletedTask; } },
        new("Ja, avinstaller", consequence) { OnAccept = async () => { CloseMenus(); await action(); } },
    };

    private List<MenuEntry> BuildPropertiesMenu(TileVm t)
    {
        static MenuEntry Row(string title, string detail) => new(title, detail) { KeepOpen = true, OnAccept = () => Task.CompletedTask };
        var rows = new List<MenuEntry> { Row("Butikk", t.StoreName.Length > 0 ? t.StoreName : "Egen app") };

        rows.Add(Row("Spilletid", _settings.Data.PlaySeconds.TryGetValue(t.Key, out var secs) && FormatDuration(secs) is { Length: > 0 } dur ? dur : "ingen registrert"));
        long ts = LastPlayedOf(t);
        rows.Add(Row("Sist spilt", ts > 0 ? LastPlayedText(ts) : "aldri"));
        if (t.Game is { Installed: > 0 } g)
            rows.Add(Row("Installert", DateTimeOffset.FromUnixTimeSeconds(g.Installed).LocalDateTime.ToString("dd.MM.yyyy")));
        var folder = t.Game?.InstallPath ?? t.InstallDir;
        rows.Add(Row("Mappe", string.IsNullOrWhiteSpace(folder) ? "ukjent" : folder));
        return rows;
    }

    private async Task UninstallGogAsync(TileVm t)
    {
        ShowToast($"Avinstallerer {t.Name}…");
        var id = t.Key[4..];
        var (ok, msg) = await Task.Run(() => GogService.Uninstall(id));
        if (ok)
        {
            _settings.Data.PlaySeconds.Remove(t.Key);
            _settings.Data.LastLaunch.Remove(t.Key);
            _settings.Save();
            await RescanAsync(false);
            ShowToast($"{t.Name} er avinstallert");
        }
        else ShowToast("Avinstallering feilet: " + msg);
    }

    private async Task UninstallEpicAsync(TileVm t)
    {
        var app = t.Key[5..];
        ShowToast($"Avinstallerer {t.Name}…");
        try
        {
            var (code, output, err) = await LegendaryService.RunAsync($"-y uninstall {app}", 900);
            if (code == 0)
            {
                _settings.Data.PlaySeconds.Remove(t.Key);
                _settings.Data.LastLaunch.Remove(t.Key);
                _settings.Save();
                await RescanAsync(false);
                ShowToast($"{t.Name} er avinstallert");
            }
            else
            {
                var msg = (err + output).Trim();
                ShowToast($"Avinstallering feilet: {(msg.Length > 120 ? msg[^120..] : msg)}");
            }
        }
        catch (Exception ex)
        {
            ShowToast("Avinstallering feilet: " + ex.Message);
        }
    }

    // ---------- Spillvolum ----------

    /// <summary>Tekst som finnes i exe-stien til spillets prosesser (mappen spillet ligger i).</summary>
    private static string? GameMarker(TileVm t)
    {
        if (t.Game?.InstallPath is { Length: > 0 } p) return p.TrimEnd('\\', '/') + "\\";
        if (t.Game?.InstallDir is { Length: > 0 } n) return "\\common\\" + n + "\\";
        if (t.InstallDir is { Length: > 0 } s) return s.TrimEnd('\\', '/') + "\\";
        return null;
    }

    private void SetGameVolume(TileVm t, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent == 100) _settings.Data.GameVolume.Remove(t.Key);
        else _settings.Data.GameVolume[t.Key] = percent;
        _settings.Save();
        if (GameMarker(t) is { } marker) AudioControl.SetDirVolume(marker, percent); // virker med en gang hvis spillet kjører
    }

    /// <summary>Windows lager lyd-økten først når spillet begynner å spille lyd, så vi prøver i ca. ett minutt etter start.</summary>
    private async Task ApplyGameVolumeAsync(TileVm t)
    {
        if (GameMarker(t) is not { } marker) return;
        for (int i = 0; i < 30; i++)
        {
            if (!_settings.Data.GameVolume.TryGetValue(t.Key, out var v)) return; // tilbake til 100 %: ikke rør
            AudioControl.SetDirVolume(marker, v);
            await Task.Delay(2000);
        }
    }

    private void StartMulti(TileVm first)
    {
        _multi = true;
        _picked.Clear();
        if (first.Kind != TileKind.Add) _picked.Add(first.Key);
        _sound.Play(Sfx.Select);
        RebuildTiles(first.Key);
        ShowToast("Velg flere: A merker spill, ⧉ gir handlinger, B avslutter");
    }

    private void EndMulti()
    {
        _multi = false;
        _picked.Clear();
        RebuildTiles();
    }

    private List<MenuEntry> BuildMultiMenu()
    {
        var d = _settings.Data;
        var keys = _picked.ToList();
        var list = new List<MenuEntry>();
        if (keys.Count == 0)
        {
            list.Add(new("Ingen spill er valgt", "gå tilbake og merk med A") { KeepOpen = true, OnAccept = () => Task.CompletedTask });
        }
        else
        {
            int toHide = keys.Count(k => !d.Hidden.Contains(k));
            if (toHide > 0)
                list.Add(new($"Skjul valgte ({toHide})", "flytt til Skjult-fanen")
                {
                    OnAccept = () =>
                    {
                        CloseMenus();
                        foreach (var k in keys) if (!d.Hidden.Contains(k)) d.Hidden.Add(k);
                        _settings.Save();
                        EndMulti();
                        ShowToast(d.ShowHiddenTab ? $"{toHide} spill er skjult" : $"{toHide} spill er skjult. Slå på Skjult-fanen i Innstillinger for å se dem");
                        return Task.CompletedTask;
                    },
                });
            int toShow = keys.Count(k => d.Hidden.Contains(k));
            if (toShow > 0)
                list.Add(new($"Vis igjen valgte ({toShow})", "flytt tilbake til biblioteket")
                {
                    OnAccept = () =>
                    {
                        CloseMenus();
                        d.Hidden.RemoveAll(k => keys.Contains(k));
                        _settings.Save();
                        EndMulti();
                        ShowToast($"{toShow} spill vises igjen");
                        return Task.CompletedTask;
                    },
                });
            bool allFav = keys.All(_settings.IsFavorite);
            list.Add(new(allFav ? "Fjern favoritt fra valgte" : "Favoritt for valgte", allFav ? "★ fjernes" : "★ legges på alle")
            {
                OnAccept = () =>
                {
                    CloseMenus();
                    foreach (var k in keys) if (_settings.IsFavorite(k) == allFav) _settings.ToggleFavorite(k);
                    EndMulti();
                    ShowToast(allFav ? "Favoritt fjernet" : "Lagt til som favoritt");
                    return Task.CompletedTask;
                },
            });
            list.Add(new("Legg i samling…", "legg alle valgte i en samling")
            {
                OnAccept = () => { OpenMenu("Samling for valgte", BuildMultiCollectionsMenu); return Task.CompletedTask; },
            });
        }
        list.Add(new("Velg alle i listen", "merker alle spillene som vises nå")
        {
            KeepOpen = true,
            OnAccept = () =>
            {
                foreach (var t in _tiles.Where(x => x.Kind != TileKind.Add)) _picked.Add(t.Key);
                RebuildTiles();
                ShowToast($"{_picked.Count} valgt");
                return Task.CompletedTask;
            },
        });
        list.Add(new("Avslutt valg", "fjerner merkingen uten å gjøre noe")
        {
            OnAccept = () => { CloseMenus(); EndMulti(); return Task.CompletedTask; },
        });
        return list;
    }

    private List<MenuEntry> BuildMultiCollectionsMenu()
    {
        var keys = _picked.ToList();
        var list = new List<MenuEntry>();
        foreach (var name in _settings.Data.Collections.Keys.ToList())
        {
            bool allIn = keys.Count > 0 && keys.All(k => _settings.Data.Collections[name].Contains(k));
            list.Add(new(name, allIn ? "✓ Alle med, A fjerner dem" : "A legger til alle valgte")
            {
                KeepOpen = true,
                OnAccept = () =>
                {
                    var col = _settings.Data.Collections[name];
                    if (allIn) col.RemoveAll(k => keys.Contains(k));
                    else foreach (var k in keys) if (!col.Contains(k)) col.Add(k);
                    _settings.Save();
                    RebuildTiles();
                    return Task.CompletedTask;
                },
            });
        }
        list.Add(new("Ny samling…", "skriv inn navn med skjermtastaturet")
        {
            OnAccept = () => { StartNaming(null, "", keys); return Task.CompletedTask; },
        });
        return list;
    }

    private void ToggleHidden(TileVm t)
    {
        if (t.Kind == TileKind.Add) return;
        var h = _settings.Data.Hidden;
        bool nowHidden = !h.Remove(t.Key);
        if (nowHidden) h.Add(t.Key);
        _settings.Save();
        _sound.Play(Sfx.Select);
        ShowToast(!nowHidden ? $"{t.Name} vises igjen"
            : _settings.Data.ShowHiddenTab ? $"{t.Name} er skjult"
            : "Skjult. Slå på Skjult-fanen i Innstillinger for å se skjulte spill.");
        RebuildTiles();
    }

    private void SetShowHiddenTab(bool on)
    {
        var d = _settings.Data;
        d.ShowHiddenTab = on;
        if (!on && _filter == FHidden) _filter = FAll; // fanen skjules mens den er valgt: hopp til «Alle»
        _settings.Save();
        RebuildTiles();
    }

    // ---------- Inndata ----------

    private void Root_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if ((_mode != Mode.Home && _mode != Mode.Keyboard) || char.IsControl(e.Character)) return;
        if (DesktopOn) return; // desktop har eget søkefelt
        _search += e.Character;
        e.Handled = true;
        RebuildTiles("");
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        Touch();
        if (_mode == Mode.Home && TryFocusDesktopSearch(e.Key)) { e.Handled = true; return; }
        if (DesktopOn && _mode == Mode.Home && TypingInTextBox()) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                await AcceptAsync();
                break;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                if (_mode == Mode.Home)
                {
                    if (_search.Length > 0) { _search = ""; RebuildTiles(""); }
                    else ToggleMenu();
                }
                else Back();
                break;
            case Windows.System.VirtualKey.Back:
                if ((_mode == Mode.Home || _mode == Mode.Keyboard) && _search.Length > 0)
                {
                    e.Handled = true;
                    _search = _search[..^1];
                    RebuildTiles("");
                }
                else if (_mode == Mode.Menu) { e.Handled = true; Back(); }
                break;
            case Windows.System.VirtualKey.F2:
                e.Handled = true;
                ToggleFavorite();
                break;
            case Windows.System.VirtualKey.F3:
                e.Handled = true;
                OpenGameMenu();
                break;
            case Windows.System.VirtualKey.PageUp:
                e.Handled = true;
                CycleFilter(-1);
                break;
            case Windows.System.VirtualKey.PageDown:
                e.Handled = true;
                CycleFilter(1);
                break;
            case Windows.System.VirtualKey.Left:
                e.Handled = true; OnDirection(PadDirection.Left); break;
            case Windows.System.VirtualKey.Right:
                e.Handled = true; OnDirection(PadDirection.Right); break;
            case Windows.System.VirtualKey.Up:
                e.Handled = true; OnDirection(PadDirection.Up); break;
            case Windows.System.VirtualKey.Down:
                e.Handled = true; OnDirection(PadDirection.Down); break;
        }
    }

    private void OnDirection(PadDirection dir)
    {
        Touch();
        if (ShotViewer.Visibility == Visibility.Visible)
        {
            if (dir == PadDirection.Left) StepShot(-1); else if (dir == PadDirection.Right) StepShot(1);
            return;
        }
        if (_mode == Mode.Busy) return;

        if (_mode == Mode.Keyboard)
        {
            MoveKeyboard(dir);
            return;
        }

        if (_mode == Mode.Home)
        {
            if (_tiles.Count == 0) return;
            if (DesktopOn) dir = dir == PadDirection.Up ? PadDirection.Left : dir == PadDirection.Down ? PadDirection.Right : dir;
            int idx = GameList.SelectedIndex;
            if (dir == PadDirection.Left) idx = Math.Max(0, idx - 1);
            else if (dir == PadDirection.Right) idx = Math.Min(_tiles.Count - 1, idx + 1);
            else return;
            GameList.SelectedIndex = idx;
            return;
        }

        // Meny
        int count = (MenuList.ItemsSource as List<MenuEntry>)?.Count ?? 0;
        if (count == 0) return;
        int i = Math.Max(0, MenuList.SelectedIndex);
        if (dir == PadDirection.Up) MenuList.SelectedIndex = Math.Max(0, i - 1);
        else if (dir == PadDirection.Down) MenuList.SelectedIndex = Math.Min(count - 1, i + 1);
        if (dir is PadDirection.Up or PadDirection.Down)
        {
            var sel = MenuList.SelectedItem;
            if (sel is not null)
            {
                MenuList.ScrollIntoView(sel);
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => MenuList.ScrollIntoView(sel));
            }
        }
        else if (MenuList.SelectedItem is MenuEntry m && m.OnAdjust is not null)
        {
            m.OnAdjust(dir == PadDirection.Left ? -1 : 1);
            RebuildMenu(false);
        }
    }

    private async Task AcceptAsync()
    {
        Touch();
        // Tastatur, mus og kontroller kan utløse «velg» samtidig: ignorer dobbelttrykk.
        var now = DateTime.UtcNow;
        if ((now - _lastAccept).TotalMilliseconds < 250) return;
        _lastAccept = now;
        if (ShotViewer.Visibility == Visibility.Visible) { StepShot(1); return; }

        if (_mode == Mode.Busy) return;

        if (_mode == Mode.Keyboard)
        {
            KbPress();
            return;
        }

        if (_mode == Mode.Home)
        {
            if (GameList.SelectedItem is not TileVm t) return;
            if (_multi)
            {
                if (t.Kind == TileKind.Add) return;
                _sound.Play(Sfx.Select);
                if (!_picked.Remove(t.Key)) _picked.Add(t.Key);
                RebuildTiles(t.Key);
                ShowToast(_picked.Count == 0 ? "Ingen valgt. B avslutter" : $"{_picked.Count} valgt. ⧉ for handlinger, B avslutter");
                return;
            }
            switch (t.Kind)
            {
                case TileKind.Game: await LaunchGameAsync(t); break;
                case TileKind.App: LaunchApp(t); break;
                default:
                    _sound.Play(Sfx.Select);
                    OpenMenu("Legg til app", BuildAddMenu);
                    break;
            }
            return;
        }

        if (MenuList.SelectedItem is not MenuEntry m) return;
        _sound.Play(Sfx.Select);
        if (m.OnAccept is not null) await m.OnAccept();
        if (m.KeepOpen && _menus.Count > 0) RebuildMenu(false);
    }

    private void Back()
    {
        Touch();
        if (ShotViewer.Visibility == Visibility.Visible) { _sound.Play(Sfx.Back); CloseShotViewer(); return; }
        if (_mode == Mode.Keyboard)
        {
            // B: tøm søket først; er det allerede tomt, lukk tastaturet.
            _sound.Play(Sfx.Back);
            if (_naming) { if (_name.Length > 0) { _name = ""; KbText.Text = "▏"; } else CloseKeyboard(); }
            else if (_search.Length > 0) { _search = ""; RebuildTiles(""); }
            else CloseKeyboard();
            return;
        }
        if (_mode == Mode.Home)
        {
            // B på hjemskjermen: avslutt flervalg, så nullstill søk, ellers gå tilbake til «Alle».
            if (_multi) { _sound.Play(Sfx.Back); EndMulti(); return; }
            if (_search.Length > 0) { _search = ""; _sound.Play(Sfx.Back); RebuildTiles(""); }
            else if (_filter != FAll) { _filter = FAll; _sound.Play(Sfx.Back); RebuildTiles(""); }
            return;
        }
        if (_mode != Mode.Menu) return;
        _sound.Play(Sfx.Back);
        _menus.Pop();
        if (_menus.Count == 0) CloseMenus();
        else RebuildMenu(true);
    }

    private void ToggleMenu()
    {
        Touch();
        if (_mode == Mode.Keyboard) { _sound.Play(Sfx.Back); CloseKeyboard(); return; }
        if (_mode == Mode.Home)
        {
            _sound.Play(Sfx.Open);
            OpenMenu("Meny", BuildMainMenu);
        }
        else if (_mode == Mode.Menu)
        {
            _sound.Play(Sfx.Back);
            CloseMenus();
        }
    }

    // ---------- Skjermtastatur ----------

    private void BuildKeyboard()
    {
        _kbKeys = new Border[KbLayout.Length][];
        for (int r = 0; r < KbLayout.Length; r++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            _kbKeys[r] = new Border[KbLayout[r].Length];
            for (int c = 0; c < KbLayout[r].Length; c++)
            {
                bool wide = KbLayout[r][c].Length > 1;
                var key = new Border
                {
                    Width = wide ? 190 : 68,
                    Height = 68,
                    CornerRadius = new CornerRadius(14),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 255, 255, 255)),
                    Child = new TextBlock
                    {
                        Text = KbLayout[r][c],
                        FontSize = wide ? 22 : 26,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                _kbKeys[r][c] = key;
                row.Children.Add(key);
            }
            KbRows.Children.Add(row);
        }
    }

    private void UpdateKeyboard()
    {
        for (int r = 0; r < _kbKeys.Length; r++)
            for (int c = 0; c < _kbKeys[r].Length; c++)
            {
                bool sel = r == _kbRow && c == _kbCol;
                var key = _kbKeys[r][c];
                key.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(sel
                    ? Windows.UI.Color.FromArgb(0xFF, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(0x26, 255, 255, 255));
                ((TextBlock)key.Child).Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(sel
                    ? Microsoft.UI.Colors.Black
                    : Microsoft.UI.Colors.White);
            }
    }

    private void OpenKeyboard()
    {
        if (_mode != Mode.Home) return;
        _mode = Mode.Keyboard;
        _kbRow = 0; _kbCol = 0;
        KbTitle.Text = _naming ? (_namePurpose == "steamkey" ? "Steam API-nøkkel (32 tegn)" : _namePurpose == "epic" ? "Epic-kode (authorizationCode)" : _namePurpose == "gog" ? "GOG-kode (code= i adressen)" : "Navn på samling") : "Søk";
        KbText.Text = (_naming ? _name : _search) + "▏";
        KeyboardOverlay.Visibility = Visibility.Visible;
        UpdateKeyboard();
        _sound.Play(Sfx.Open);
    }

    private void CloseKeyboard()
    {
        KeyboardOverlay.Visibility = Visibility.Collapsed;
        _naming = false;
        _namePurpose = "";
        _mode = Mode.Home;
        GameList.Focus(FocusState.Programmatic);
    }

    private void MoveKeyboard(PadDirection dir)
    {
        int rows = KbLayout.Length;
        switch (dir)
        {
            case PadDirection.Left: _kbCol = Math.Max(0, _kbCol - 1); break;
            case PadDirection.Right: _kbCol = Math.Min(KbLayout[_kbRow].Length - 1, _kbCol + 1); break;
            case PadDirection.Up: _kbRow = Math.Max(0, _kbRow - 1); break;
            case PadDirection.Down: _kbRow = Math.Min(rows - 1, _kbRow + 1); break;
        }
        _kbCol = Math.Min(_kbCol, KbLayout[_kbRow].Length - 1);
        // Fra bokstavrader ned til siste rad: hopp til nærmeste handlingstast.
        if (_kbRow == rows - 1 && dir == PadDirection.Down) _kbCol = Math.Min(2, _kbCol / 4);
        _sound.Play(Sfx.Move);
        UpdateKeyboard();
    }

    private void KbPress()
    {
        var key = KbLayout[_kbRow][_kbCol];
        _sound.Play(Sfx.Select);
        switch (key)
        {
            case "Ferdig": if (_naming) FinishNaming(); else CloseKeyboard(); return;
            case "⌫": KbBackspace(); return;
            case "Lim inn": _ = KbPasteAsync(); return;
            case "Mellomrom": KbAppend(" "); return;
            default: KbAppend(key); return;
        }
    }

    /// <summary>Limer inn fra utklippstavlen. Til Epic-koden hentes selve koden ut av «authorizationCode» hvis hele teksten er kopiert.</summary>
    private async Task KbPasteAsync()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                ShowToast("Utklippstavlen har ingen tekst");
                return;
            }
            var text = ((await content.GetTextAsync()) ?? "").Trim();
            if (text.Length == 0) { ShowToast("Utklippstavlen er tom"); return; }

            if (_naming && _namePurpose == "steamkey")
            {
                var kcode = new string(text.Where(char.IsLetterOrDigit).ToArray());
                if (kcode.Length == 0) { ShowToast("Fant ingen nøkkel i teksten"); return; }
                _name = kcode.Length > 40 ? kcode[..40] : kcode;
                KbText.Text = _name + "▏";
                return;
            }

            if (_naming && _namePurpose == "gog")
            {
                var gm = System.Text.RegularExpressions.Regex.Match(text, @"[?&]code=([A-Za-z0-9_\-]+)");
                var gcode = gm.Success ? gm.Groups[1].Value : new string(text.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
                if (gcode.Length == 0) { ShowToast("Fant ingen kode i teksten"); return; }
                _name = gcode.Length > 80 ? gcode[..80] : gcode;
                KbText.Text = _name + "▏";
                return;
            }

            if (_naming && _namePurpose == "epic")
            {
                var m = System.Text.RegularExpressions.Regex.Match(text, "authorizationCode\\W+([A-Za-z0-9]+)");
                var code = m.Success ? m.Groups[1].Value : new string(text.Where(char.IsLetterOrDigit).ToArray());
                if (code.Length == 0) { ShowToast("Fant ingen kode i teksten"); return; }
                _name = code.Length > 40 ? code[..40] : code;
                KbText.Text = _name + "▏";
                return;
            }

            text = text.Replace("\r", " ").Replace("\n", " ");
            if (_naming)
            {
                var joined = _name + text;
                _name = joined.Length > 24 ? joined[..24] : joined;
                KbText.Text = _name + "▏";
            }
            else
            {
                _search += text;
                RebuildTiles("");
            }
        }
        catch { ShowToast("Kunne ikke lese utklippstavlen"); }
    }

    private void KbAppend(string s)
    {
        if (_naming)
        {
            if (_name.Length < (_namePurpose == "epic" || _namePurpose == "steamkey" ? 40 : _namePurpose == "gog" ? 80 : 24)) _name += s;
            KbText.Text = _name + "▏";
            return;
        }
        _search += s;
        RebuildTiles("");
    }

    private void StartNaming(string? targetKey, string purpose = "", List<string>? multi = null)
    {
        CloseMenus();
        _naming = true;
        _namePurpose = purpose;
        _name = "";
        _nameTarget = targetKey;
        _nameMulti = multi; // nullstilles ved vanlig bruk, så en avbrutt navngiving ikke lekker inn i neste
        OpenKeyboard();
    }

    private void FinishNaming()
    {
        var name = _name.Trim();
        var target = _nameTarget;
        var purpose = _namePurpose;
        CloseKeyboard();
        if (name.Length == 0) return;
        if (purpose == "steamkey") { _ = SaveSteamKeyAsync(name); return; }
        if (purpose == "epic") { _ = EpicLoginAsync(name); return; }
        if (purpose == "gog") { _ = GogLoginAsync(name); return; }
        var cols = _settings.Data.Collections;
        if (!cols.ContainsKey(name)) cols[name] = new List<string>();
        var multi = _nameMulti;
        _nameMulti = null;
        if (multi is not null)
        {
            foreach (var k in multi) if (!cols[name].Contains(k)) cols[name].Add(k);
            _settings.Save();
            EndMulti();
            ShowToast($"Samlingen «{name}» er laget med {multi.Count} spill");
            return;
        }
        if (target is not null && !cols[name].Contains(target)) cols[name].Add(target);
        _settings.Save();
        ShowToast($"Samlingen «{name}» er laget");
        RebuildTiles(target);
    }

    private void KbBackspace()
    {
        if (_naming)
        {
            if (_name.Length == 0) return;
            _name = _name[..^1];
            _sound.Play(Sfx.Back);
            KbText.Text = _name + "▏";
            return;
        }
        if (_search.Length == 0) return;
        _search = _search[..^1];
        _sound.Play(Sfx.Back);
        RebuildTiles("");
    }

    // ---------- Meny ----------

    private void OpenMenu(string title, Func<List<MenuEntry>> build)
    {
        if (DesktopOn && title == "Nedlastinger")
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowSettingsPage(PageDownloads));
            return;
        }
        if (DesktopOn && title == "Innstillinger")
        {
            // Desktop: innstillingene er en egen side. Vises litt senere så en åpen flyout rekker å lukkes.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowSettingsPage());
            return;
        }
        _menus.Push((title, build));
        _mode = Mode.Menu;
        RebuildMenu(true);
    }

    private void RebuildMenu(bool reset)
    {
        if (_menus.Count == 0) return;
        if (UseFlyoutMenu()) { ShowFlyoutMenu(); return; }
        ApplyMenuStyle();
        MenuOverlay.Visibility = Visibility.Visible;
        var (title, build) = _menus.Peek();
        int idx = reset ? 0 : Math.Max(0, MenuList.SelectedIndex);
        var items = build();

        _quiet = true;
        MenuTitle.Text = title;
        MenuList.ItemsSource = items;
        if (items.Count > 0)
        {
            MenuList.SelectedIndex = Math.Min(idx, items.Count - 1);
            if (MenuList.SelectedItem is not null) MenuList.ScrollIntoView(MenuList.SelectedItem);
        }
        _quiet = false;

        DispatcherQueue.TryEnqueue(() => MenuList.Focus(FocusState.Programmatic));
    }

    private void CloseMenus()
    {
        _menus.Clear();
        _flyGen++;
        var fl = _flyout; _flyout = null;
        try { fl?.Hide(); } catch { }
        MenuOverlay.Visibility = Visibility.Collapsed;
        _mode = Mode.Home;
        GameList.Focus(FocusState.Programmatic);
    }

    private void MenuList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_quiet && _mode == Mode.Menu && MenuList.SelectedItem is not null)
            _sound.Play(Sfx.Move);
    }

    /// <summary>Desktop: raden under musepekeren blir markert (ellers står markeringen igjen på første rad).</summary>
    private void MenuList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!DesktopOn || _mode != Mode.Menu) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is MenuEntry m && !ReferenceEquals(MenuList.SelectedItem, m))
        {
            _quiet = true;
            MenuList.SelectedItem = m;
            _quiet = false;
        }
    }

    private void MenuList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MenuEntry m)
        {
            MenuList.SelectedItem = m;
            _ = AcceptAsync();
        }
    }

    private List<MenuEntry> BuildAppVolumeMenu()
    {
        var list = new List<MenuEntry>();
        foreach (var app in AudioControl.Apps())
        {
            var a = app;
            list.Add(new(a.Name, a.Muted ? "Dempet" : $"◄ {a.Volume} % ►")
            {
                KeepOpen = true,
                OnAccept = () => { a.Muted = !a.Muted; return Task.CompletedTask; },
                OnAdjust = dir =>
                {
                    if (a.Muted) a.Muted = false;
                    a.Volume += dir * 5;
                },
            });
        }
        if (list.Count == 0) list.Add(new("Ingen apper spiller lyd akkurat nå"));
        return list;
    }

    private List<MenuEntry> BuildAudioMenu()
    {
        var list = new List<MenuEntry>();
        foreach (var dev in AudioControl.Outputs())
        {
            var d = dev;
            list.Add(new(d.Name, d.IsDefault ? "✓ I bruk" : "")
            {
                OnAccept = () =>
                {
                    bool ok = AudioControl.SetDefault(d.Id);
                    CloseMenus();
                    ShowToast(ok ? $"Lyd: {d.Name}" : "Klarte ikke bytte lydutgang");
                    return Task.CompletedTask;
                },
            });
        }
        if (list.Count == 0) list.Add(new("Fant ingen lydenheter"));
        return list;
    }

    private List<MenuEntry> BuildMainMenu()
    {
        var d = _settings.Data;
        var list = new List<MenuEntry>();

        if (_running is not null)
        {
            list.Add(new("Gå tilbake til spillet", _running.Name)
            {
                OnAccept = () => { CloseMenus(); ResumeGame(); return Task.CompletedTask; },
            });
        }

        // Favoritt, Samlinger, Skjul og Fjern fra listen ligger i spillmenyen (View/F3). ZenTune blir her.
        if (GameList.SelectedItem is TileVm { Kind: TileKind.Game, Game: not null } tuneTile)
        {
            list.Add(new("Optimaliser med ZenTune", tuneTile.Name)
            {
                OnAccept = () => OpenTuneAsync(tuneTile),
            });
        }

        list.Add(new("Søk", "skjermtastatur (X)")
        {
            OnAccept = () => { CloseMenus(); OpenKeyboard(); return Task.CompletedTask; },
        });
        if (PluginOn("random"))
            list.Add(new("Tilfeldig spill", "velger et installert spill fra listen")
            { OnAccept = () => { CloseMenus(); PickRandom(); return Task.CompletedTask; } });
        list.Add(new("Bibliotek…", "søk etter spill, legg til app")
        {
            OnAccept = () => { OpenMenu("Bibliotek", BuildLibraryMenu); return Task.CompletedTask; },
        });

        list.Add(new("Nedlastinger…", _dlQueue.Count == 0 ? "ingen pågår" : $"{_dlQueue.Count} i køen")
        {
            OnAccept = () => { OpenMenu("Nedlastinger", BuildDownloadsMenu); return Task.CompletedTask; },
        });
        list.Add(new("Streaming…", "spill fra en annen PC (Moonlight)")
        {
            OnAccept = () =>
            {
                if (DesktopOn) { CloseMenus(); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ShowSettingsPage(PageStreaming)); }
                else { _settingsCat = CatStreaming; OpenMenu("Innstillinger", BuildSettingsMenu); }
                return Task.CompletedTask;
            },
        });
        list.Add(new("Kontoer…", "Epic-innlogging og bibliotek")
        {
            OnAccept = () => { OpenMenu("Kontoer", BuildAccountsMenu); return Task.CompletedTask; },
        });

        list.Add(new("Systemvolum", AudioControl.GetMute() ? "Dempet (trykk for å slå på lyd)" : $"◄ {Math.Max(AudioControl.GetVolume(), 0)} % ►")
        {
            KeepOpen = true,
            OnAccept = () => { AudioControl.SetMute(!AudioControl.GetMute()); return Task.CompletedTask; },
            OnAdjust = dir =>
            {
                if (AudioControl.GetMute()) AudioControl.SetMute(false);
                AudioControl.SetVolume(AudioControl.GetVolume() + dir * 5);
            },
        });
        list.Add(new("Lyd…", "appvolum, lydutgang, menylyder")
        {
            OnAccept = () => { OpenMenu("Lyd", BuildSoundMenu); return Task.CompletedTask; },
        });
        list.Add(new("Innstillinger…", "kontroller som mus, Windows")
        {
            OnAccept = () => { OpenMenu("Innstillinger", BuildSettingsMenu); return Task.CompletedTask; },
        });
        list.Add(new("Strøm…", "hvilemodus, start på nytt, slå av, avslutt")
        {
            OnAccept = () => { OpenMenu("Strøm", BuildPowerMenu); return Task.CompletedTask; },
        });
        return list;
    }

    private bool _installing;

    private List<MenuEntry> BuildInstallDiskMenu(TileVm t)
    {
        var list = new List<MenuEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                var root = d.Name;
                var free = d.AvailableFreeSpace / 1_000_000_000L;
                var folderName = t.Store == "GOG" ? "Games" : "Epic Games";
                list.Add(new($"{root}", $"{free} GB ledig  •  {Path.Combine(root, folderName)}")
                {
                    OnAccept = () =>
                    {
                        CloseMenus();
                        _ = InstallEpicAsync(t, Path.Combine(root, folderName));
                        return Task.CompletedTask;
                    },
                });
            }
            catch { }
        }
        if (list.Count == 0) list.Add(new("Fant ingen disker"));
        return list;
    }

    private sealed class DlItem
    {
        public TileVm Tile = null!;
        public string BasePath = "";
        public string State = "queued"; // queued | active
        public string Detail = "";
        public double Percent = -1;
        public CancellationTokenSource? Cts;
    }
    private readonly List<DlItem> _dlQueue = new();
    private readonly List<string> _dlDone = new();
    private bool _pumping;

    /// <summary>Legger et Epic/GOG-spill i nedlastingskøen. Én nedlasting kjører om gangen.</summary>
    private Task InstallEpicAsync(TileVm t, string basePath)
    {
        if (_dlQueue.Any(d => d.Tile.Key == t.Key)) { ShowToast("Allerede i køen"); return Task.CompletedTask; }
        _dlQueue.Add(new DlItem { Tile = t, BasePath = basePath });
        UpdateDlButton();
        if (_dlQueue.Count > 1) ShowToast($"{t.Name} lagt i kø");
        _ = PumpQueueAsync();
        return Task.CompletedTask;
    }

    private async Task PumpQueueAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (true)
            {
                var next = _dlQueue.FirstOrDefault(d => d.State == "queued");
                if (next is null) break;
                await RunInstallAsync(next);
            }
        }
        finally { _pumping = false; }
    }

    private List<MenuEntry> BuildDownloadsMenu()
    {
        var list = new List<MenuEntry>();
        foreach (var d in _dlQueue.ToList())
        {
            var it = d;
            var detail = it.State == "active"
                ? (it.Detail.Length > 0 ? it.Detail : "Laster ned…")
                : $"I kø (nr. {_dlQueue.IndexOf(it)})";
            list.Add(new(it.Tile.Name, detail)
            {
                OnAccept = () => { OpenMenu(it.Tile.Name, () => BuildDownloadItemMenu(it)); return Task.CompletedTask; },
            });
        }
        if (_dlQueue.Count == 0) list.Add(new("Ingen nedlastinger", "Epic og GOG går via køen. Steam vises i banneret."));
        foreach (var done in Enumerable.Reverse(_dlDone).Take(8)) list.Add(new(done, "ferdig"));
        return list;
    }

    private List<MenuEntry> BuildDownloadItemMenu(DlItem it)
    {
        var list = new List<MenuEntry>();
        if (it.State == "queued")
        {
            list.Add(new("Flytt først i køen")
            {
                OnAccept = () =>
                {
                    if (_dlQueue.Remove(it))
                    {
                        var firstQueued = _dlQueue.FindIndex(d => d.State == "queued");
                        _dlQueue.Insert(firstQueued < 0 ? _dlQueue.Count : firstQueued, it);
                    }
                    Back();
                    return Task.CompletedTask;
                },
            });
        }
        list.Add(new("Avbryt nedlasting", it.State == "active" ? "stopper; delvis nedlastede filer blir liggende" : "fjerner fra køen")
        {
            OnAccept = () =>
            {
                if (it.State == "active") it.Cts?.Cancel();
                else _dlQueue.Remove(it);
                Back();
                return Task.CompletedTask;
            },
        });
        return list;
    }

    private async Task RunInstallAsync(DlItem it)
    {
        var t = it.Tile; var basePath = it.BasePath;
        it.State = "active";
        it.Cts = new CancellationTokenSource();
        var ct = it.Cts.Token;
        string outcome = "";
        bool gog = t.Key.StartsWith("gog:", StringComparison.Ordinal);
        bool epic = t.Key.StartsWith("epic:", StringComparison.Ordinal);
        if (!gog && !epic) return;
        var app = t.Key[(gog ? 4 : 5)..];
        // Et spill du installerer skal du se i «Alle»: ta det ut av Skjult.
        if (_settings.Data.Hidden.Remove(t.Key)) { _settings.Save(); RebuildTiles(); }
        _installing = true;
        InstallTitle.Text = $"Installerer {t.Name}";
        InstallBar.IsIndeterminate = true;
        InstallDetail.Text = "Forbereder…";
        InstallBanner.Visibility = Visibility.Visible;

        double percent = -1; string speed = "", eta = "", lastLine = "";
        var lastUi = DateTime.MinValue;
        void OnLine(string line)
        {
            // Legendary: «Progress: 12.34% (…), ETA: 00:08:00» og «Download - 25.1 MiB/s». gogdl: formatet er ikke verifisert,
            // så vi tar første «NN %» vi ser og viser ellers siste utskriftslinje.
            var p = System.Text.RegularExpressions.Regex.Match(line, gog ? @"([\d.,]+)\s*%" : @"Progress:\s*([\d.,]+)\s*%");
            if (p.Success && double.TryParse(p.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pv) && pv <= 100)
                percent = pv;
            var e = System.Text.RegularExpressions.Regex.Match(line, @"ETA:\s*([\d:]+)");
            if (e.Success) eta = e.Groups[1].Value;
            var s2 = System.Text.RegularExpressions.Regex.Match(line, @"(?:Download\s*-\s*)?([\d.,]+\s*[KMG]i?B/s)");
            if (s2.Success) speed = s2.Groups[1].Value;
            lastLine = line.Length > 70 ? line[..70] : line;

            if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 700) return;
            lastUi = DateTime.UtcNow;
            var pct = percent; var spd = speed; var rest = eta; var ll = lastLine;
            DispatcherQueue.TryEnqueue(() =>
            {
                it.Percent = pct;
                if (pct >= 0) { InstallBar.IsIndeterminate = false; InstallBar.Value = pct; }
                var parts = new List<string>();
                if (pct >= 0) parts.Add($"{pct:0.#} %");
                if (spd.Length > 0) parts.Add(spd);
                if (rest.Length > 0) parts.Add($"{rest} igjen");
                if (parts.Count > 0) InstallDetail.Text = string.Join("  •  ", parts);
                else if (ll.Length > 0) InstallDetail.Text = ll;
                it.Detail = InstallDetail.Text;
            });
        }

        try
        {
            var (ok, msg) = gog
                ? await GogService.InstallAsync(app, t.Name, basePath, OnLine, ct)
                : await LegendaryService.InstallAsync(app, basePath, OnLine, ct);
            InstallBanner.Visibility = Visibility.Collapsed;
            outcome = ok ? t.Name + " (installert)" : t.Name + " (feilet)";
            ShowToast(ok ? $"{t.Name} er installert" : "Installasjon feilet: " + msg);
            await RescanAsync(false); // også ved «allerede installert»: da må flisen likevel oppdateres
        }
        catch (OperationCanceledException)
        {
            InstallBanner.Visibility = Visibility.Collapsed;
            outcome = t.Name + " (avbrutt)";
            ShowToast($"{t.Name}: nedlasting avbrutt");
        }
        catch (Exception ex)
        {
            InstallBanner.Visibility = Visibility.Collapsed;
            outcome = t.Name + " (feilet)";
            ShowToast("Installasjon feilet: " + ex.Message);
        }
        finally
        {
            _installing = false;
            _dlQueue.Remove(it);
            UpdateDlButton();
            if (outcome.Length > 0) { _dlDone.Add(outcome); if (_dlDone.Count > 20) _dlDone.RemoveAt(0); }
            it.Cts?.Dispose();
        }
    }

    private async Task EpicLoginAsync(string code)
    {
        ShowToast("Logger inn på Epic…");
        var ok = await LegendaryService.TryAuthWithCodeAsync(code.ToLowerInvariant());
        if (!ok) { ShowToast("Epic-innlogging feilet: " + LegendaryService.LastError); return; }
        var games = await LegendaryService.ListLibraryAsync();
        ShowToast(games.Count > 0 ? $"Innlogget på Epic. Biblioteket har {games.Count} spill" : "Innlogget på Epic");
        if (games.Count > 0) await RescanAsync(false);
    }

    private List<MenuEntry> BuildSteamMenu()
    {
        var d = _settings.Data;
        var list = new List<MenuEntry>();
        list.Add(new("Steam: API-nøkkel", d.SteamApiKey.Length > 0 ? "lagt inn" : "ikke lagt inn, trengs for listen over spill du eier")
        {
            OnAccept = () => { StartNaming(null, "steamkey"); return Task.CompletedTask; },
        });
        list.Add(new("Steam: lag nøkkel", "åpner Steams side i nettleseren (domene: skriv localhost)")
        {
            KeepOpen = true,
            OnAccept = () =>
            {
                try { Process.Start(new ProcessStartInfo(SteamOwned.KeyPageUrl) { UseShellExecute = true }); }
                catch (Exception ex) { ShowToast("Kunne ikke åpne nettleseren: " + ex.Message); }
                return Task.CompletedTask;
            },
        });
        if (d.SteamApiKey.Length > 0)
        {
            list.Add(new("Steam: hent bibliotek", "spill du eier (profilens spilldetaljer må være offentlige)")
            {
                KeepOpen = true,
                OnAccept = async () => await FetchSteamLibraryAsync(),
            });
            list.Add(new("Steam: fjern nøkkelen", "sletter nøkkelen og listen over uinstallerte spill")
            {
                OnAccept = async () =>
                {
                    CloseMenus();
                    _settings.Data.SteamApiKey = "";
                    _settings.Save();
                    try { if (File.Exists(SteamOwned.CachePath)) File.Delete(SteamOwned.CachePath); } catch { }
                    await RescanAsync(false);
                    ShowToast("Steam-nøkkelen er fjernet");
                },
            });
        }
        return list;
    }

    private async Task SaveSteamKeyAsync(string key)
    {
        key = key.Trim();
        if (key.Length != 32 || !key.All(Uri.IsHexDigit)) { ShowToast("Nøkkelen skal være 32 tegn (0-9, A-F). Prøv igjen"); return; }
        _settings.Data.SteamApiKey = key.ToUpperInvariant();
        _settings.Save();
        ShowToast("Nøkkelen er lagret. Henter biblioteket…");
        await FetchSteamLibraryAsync();
    }

    private async Task FetchSteamLibraryAsync()
    {
        ShowToast("Henter Steam-biblioteket…");
        var games = await SteamOwned.ListLibraryAsync(_settings.Data.SteamApiKey);
        if (games.Count == 0) { ShowToast("Fikk ingen spill: " + SteamOwned.LastError); return; }
        await RescanAsync(false);
        ShowToast($"Steam-biblioteket: {games.Count} spill (se «Ikke installert»)");
    }

    private bool _steamWatching;

    /// <summary>
    /// Starter installasjon via Steam (steam://install/id) og følger fremdriften i appmanifest_id.acf.
    /// Steam velger selv bibliotek og kan vise sin egen dialog. Ferdig når StateFlags har bit 4 (fullt installert).
    /// </summary>
    private async Task StartSteamInstallAsync(TileVm t)
    {
        if (!int.TryParse(t.Key[6..], out var appId)) return;
        if (_steamWatching) { ShowToast("Følger allerede en Steam-installasjon"); return; }
        try { Process.Start(new ProcessStartInfo($"steam://install/{appId}") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("Kunne ikke åpne Steam: " + ex.Message); return; }
        ShowToast("Bekreft installasjonen i Steam");

        _steamWatching = true;
        bool bannerOn = false;
        try
        {
            var started = DateTime.UtcNow;
            bool seen = false;
            long bestDone = 0;
            while (DateTime.UtcNow - started < TimeSpan.FromHours(8))
            {
                await Task.Delay(3000);
                var p = await Task.Run(() => SteamOwned.ReadProgress(appId)); // mappemåling: ikke på UI-tråden
                if (p is null)
                {
                    // Ingen fil: dialogen kan fortsatt være åpen. Gi opp etter 5 minutter uten spor.
                    if (!seen && DateTime.UtcNow - started > TimeSpan.FromMinutes(5)) break;
                    if (seen) break; // filen forsvant: installasjonen ble avbrutt
                    continue;
                }
                seen = true;
                var (flags, done0, total) = p.Value;
                // Aldri bakover (mappen tømmes når Steam flytter filene til sluttmappen).
                if (done0 > bestDone) bestDone = done0;
                var done = bestDone;
                if ((flags & 4) != 0) { ShowToast($"{t.Name} er installert"); await RescanAsync(false); return; }
                if (!_installing)
                {
                    bannerOn = true;
                    InstallTitle.Text = $"Installerer {t.Name} (Steam)";
                    InstallBanner.Visibility = Visibility.Visible;
                    if (total > 0)
                    {
                        InstallBar.IsIndeterminate = false;
                        InstallBar.Value = Math.Clamp(done * 100.0 / total, 0, 100);
                        InstallDetail.Text = $"{done * 100.0 / total:0.#} %  •  {done / 1_000_000_000.0:0.0} av {total / 1_000_000_000.0:0.0} GB";
                    }
                    else
                    {
                        InstallBar.IsIndeterminate = true;
                        InstallDetail.Text = "Venter på Steam…";
                    }
                }
            }
        }
        catch { }
        finally
        {
            _steamWatching = false;
            if (bannerOn && !_installing) InstallBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async Task GogLoginAsync(string code)
    {
        ShowToast("Logger inn på GOG…");
        var ok = await GogService.TryAuthWithCodeAsync(code);
        if (!ok) { ShowToast("GOG-innlogging feilet: " + GogService.LastError); return; }
        var games = await GogService.ListLibraryAsync();
        ShowToast(games.Count > 0 ? $"Innlogget på GOG. Biblioteket har {games.Count} spill" : "Innlogget på GOG, men fikk ingen spill: " + GogService.LastError);
        if (games.Count > 0) await RescanAsync(false);
    }

    private List<MenuEntry> BuildAccountsMenu() => new()
    {
        new("Epic Games…", "innlogging og bibliotek")
        {
            OnAccept = () => { OpenMenu("Epic Games", BuildEpicMenu); return Task.CompletedTask; },
        },
        new("GOG…", "innlogging og bibliotek")
        {
            OnAccept = () => { OpenMenu("GOG", BuildGogMenu); return Task.CompletedTask; },
        },
        new("Steam…", "API-nøkkel og spill du eier")
        {
            OnAccept = () => { OpenMenu("Steam", BuildSteamMenu); return Task.CompletedTask; },
        },
    };

    private List<MenuEntry> BuildGogMenu()
    {
        var list = new List<MenuEntry>();
        list.Add(new("GOG: logg inn", "åpner et innloggingsvindu, resten skjer av seg selv")
        {
            OnAccept = async () =>
            {
                CloseMenus();
                if (!GogService.IsAvailable)
                {
                    ShowToast("Laster ned gogdl…");
                    if (!await GogService.EnsureToolAsync()) { ShowToast("Kunne ikke laste ned gogdl: " + GogService.LastError); return; }
                }
                var code = await LoginBrowser.GetCodeAsync("Logg inn på GOG", GogService.LoginUrl, LoginBrowser.Kind.Gog);
                if (code is null) { ShowToast("Innlogging avbrutt"); return; }
                await GogLoginAsync(code);
            },
        });
        if (!GogService.IsAvailable)
        {
            list.Add(new("GOG: last ned gogdl", "verktøyet som kobler til GOG-kontoen")
            {
                KeepOpen = true,
                OnAccept = async () =>
                {
                    ShowToast("Laster ned gogdl…");
                    var ok = await GogService.EnsureToolAsync();
                    ShowToast(ok ? "gogdl er klar" : "Kunne ikke laste ned gogdl: " + GogService.LastError);
                },
            });
            return list;
        }
        list.Add(new("GOG: logg inn manuelt", "kopier adressen fra nettleseren, med Lim inn")
        {
            OnAccept = () =>
            {
                try { Process.Start(new ProcessStartInfo(GogService.LoginUrl) { UseShellExecute = true }); }
                catch { }
                StartNaming(null, "gog");
                return Task.CompletedTask;
            },
        });
        list.Add(new("GOG: hent bibliotek", "teller spillene på kontoen")
        {
            KeepOpen = true,
            OnAccept = async () =>
            {
                ShowToast("Henter GOG-biblioteket…");
                var games = await GogService.ListLibraryAsync();
                ShowToast(games.Count > 0 ? $"GOG-biblioteket: {games.Count} spill (se «Ikke installert»)" : "Fikk ingen spill: " + GogService.LastError);
                if (games.Count > 0) await RescanAsync(false);
            },
        });
        list.Add(new("GOG: oppdater gogdl", "laster ned nyeste versjon")
        {
            KeepOpen = true,
            OnAccept = async () =>
            {
                ShowToast("Oppdaterer gogdl…");
                var ok = await GogService.EnsureToolAsync(force: true);
                ShowToast(ok ? "gogdl er oppdatert" : "Kunne ikke oppdatere gogdl: " + GogService.LastError);
            },
        });
        return list;
    }

    private List<MenuEntry> BuildEpicMenu()
    {
        var list = new List<MenuEntry>();
        list.Add(new("Epic: logg inn", "åpner et innloggingsvindu, resten skjer av seg selv")
        {
            OnAccept = async () =>
            {
                CloseMenus();
                if (!LegendaryService.IsAvailable)
                {
                    ShowToast("Laster ned Legendary…");
                    if (!await LegendaryService.EnsureToolAsync()) { ShowToast("Kunne ikke laste ned Legendary"); return; }
                }
                var code = await LoginBrowser.GetCodeAsync("Logg inn på Epic Games", LoginBrowser.EpicUrl, LoginBrowser.Kind.Epic);
                if (code is null) { ShowToast("Innlogging avbrutt"); return; }
                await EpicLoginAsync(code);
            },
        });
        if (!LegendaryService.IsAvailable)
        {
            list.Add(new("Epic: last ned Legendary", "verktøyet som kobler til Epic-kontoen")
            {
                KeepOpen = true,
                OnAccept = async () =>
                {
                    ShowToast("Laster ned Legendary…");
                    var ok = await LegendaryService.EnsureToolAsync();
                    ShowToast(ok ? "Legendary er klar" : "Kunne ikke laste ned Legendary");
                },
            });
            return list;
        }

        list.Add(new("Epic: logg inn manuelt", "kode fra nettleseren, med Lim inn")
        {
            OnAccept = () =>
            {
                try { Process.Start(new ProcessStartInfo("https://legendary.gl/epiclogin") { UseShellExecute = true }); }
                catch { }
                StartNaming(null, "epic");
                return Task.CompletedTask;
            },
        });
        list.Add(new("Epic: hent bibliotek", "teller spillene på kontoen")
        {
            KeepOpen = true,
            OnAccept = async () =>
            {
                ShowToast("Henter Epic-biblioteket…");
                var games = await LegendaryService.ListLibraryAsync();
                ShowToast(games.Count > 0 ? $"Epic-biblioteket: {games.Count} spill (se «Ikke installert»)" : "Fikk ingen spill: " + LegendaryService.LastError);
                if (games.Count > 0) await RescanAsync(false);
            },
        });
        list.Add(new("Epic: oppdater Legendary", "laster ned nyeste versjon")
        {
            KeepOpen = true,
            OnAccept = async () =>
            {
                ShowToast("Oppdaterer Legendary…");
                var ok = await LegendaryService.EnsureToolAsync(force: true);
                ShowToast(ok ? "Legendary er oppdatert" : "Kunne ikke oppdatere Legendary");
            },
        });
        return list;
    }

    private List<MenuEntry> BuildLibraryMenu() => new()
    {
        new("Søk etter nye spill", "Steam, Epic, GOG, Ubisoft")
        {
            OnAccept = () =>
            {
                CloseMenus();
                ShowToast("Søker etter spill…");
                return RescanAsync(true);
            },
        },
        new("Legg til app", "nettleser, program, nettside")
        {
            OnAccept = () => { OpenMenu("Legg til app", BuildAddMenu); return Task.CompletedTask; },
        },
        new("Sortering", $"◄ {SortNames[_settings.Data.SortMode]} ►")
        {
            KeepOpen = true,
            OnAccept = () => { CycleSort(1); return Task.CompletedTask; },
            OnAdjust = dir => CycleSort(dir),
        },
        new("Samlinger…", "slett egne samlinger")
        {
            OnAccept = () => { OpenMenu("Samlinger", BuildDeleteCollectionsMenu); return Task.CompletedTask; },
        },
    };

    private void CycleSort(int dir)
    {
        var d = _settings.Data;
        d.SortMode = (d.SortMode + dir + SortNames.Length) % SortNames.Length;
        _settings.Save();
        RebuildTiles();
    }

    private List<MenuEntry> BuildDeleteCollectionsMenu()
    {
        var list = new List<MenuEntry>();
        foreach (var name in _settings.Data.Collections.Keys.ToList())
        {
            list.Add(new($"Slett «{name}»", $"{_settings.Data.Collections[name].Count} oppføringer")
            {
                KeepOpen = true,
                OnAccept = () =>
                {
                    _settings.Data.Collections.Remove(name);
                    _settings.Save();
                    if (_filter >= FBase) _filter = FAll;
                    RebuildTiles();
                    return Task.CompletedTask;
                },
            });
        }
        if (list.Count == 0) list.Add(new("Ingen samlinger", "lag en via et spill sin meny") { KeepOpen = true, OnAccept = () => Task.CompletedTask });
        return list;
    }

    private List<MenuEntry> BuildCollectionsMenu(TileVm tile)
    {
        var list = new List<MenuEntry>();
        foreach (var name in _settings.Data.Collections.Keys.ToList())
        {
            bool inIt = _settings.Data.Collections[name].Contains(tile.Key);
            list.Add(new(name, inIt ? "✓ Med" : "Ikke med")
            {
                KeepOpen = true,
                OnAccept = () =>
                {
                    var keys = _settings.Data.Collections[name];
                    if (!keys.Remove(tile.Key)) keys.Add(tile.Key);
                    _settings.Save();
                    RebuildTiles(tile.Key);
                    return Task.CompletedTask;
                },
            });
        }
        list.Add(new("Ny samling…", "skriv inn navn med skjermtastaturet")
        {
            OnAccept = () => { StartNaming(tile.Key); return Task.CompletedTask; },
        });
        return list;
    }

    private List<MenuEntry> BuildSoundMenu()
    {
        var d = _settings.Data;
        return new List<MenuEntry>
        {
            new("Appvolum", "spill og programmer som spiller lyd")
            {
                OnAccept = () => { OpenMenu("Appvolum", BuildAppVolumeMenu); return Task.CompletedTask; },
            },
            new("Lydutgang", AudioControl.Outputs().FirstOrDefault(x => x.IsDefault)?.Name ?? "Ukjent")
            {
                OnAccept = () => { OpenMenu("Lydutgang", BuildAudioMenu); return Task.CompletedTask; },
            },
            new("Bakgrunnsmusikk", d.MusicEnabled ? "På – spiller mens ZenLauncher er synlig" : "Av")
            {
                KeepOpen = true,
                OnAccept = () => { ToggleMusic(!d.MusicEnabled); return Task.CompletedTask; },
                OnAdjust = dir => ToggleMusic(dir > 0),
            },
            new("Musikkvolum", $"◄ {d.MusicVolume} % ►")
            {
                KeepOpen = true,
                OnAccept = () => Task.CompletedTask,
                OnAdjust = dir =>
                {
                    d.MusicVolume = Math.Clamp(d.MusicVolume + dir * 5, 0, 100);
                    _settings.Save();
                    _music.Apply();
                },
            },
            new("Musikkmappe", $"{BackgroundMusic.CountFiles()} filer – åpne mappen og legg inn egne låter")
            {
                KeepOpen = true,
                OnAccept = () =>
                {
                    try
                    {
                        Directory.CreateDirectory(BackgroundMusic.Folder);
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BackgroundMusic.Folder}\"") { UseShellExecute = true });
                    }
                    catch { }
                    return Task.CompletedTask;
                },
            },
            new("Menylyder", d.SoundEnabled ? "På" : "Av")
            {
                KeepOpen = true,
                OnAccept = () => { d.SoundEnabled = !d.SoundEnabled; _settings.Save(); return Task.CompletedTask; },
                OnAdjust = dir => { d.SoundEnabled = dir > 0; _settings.Save(); },
            },
            new("Volum på menylyder", $"◄ {d.SoundVolume} % ►")
            {
                KeepOpen = true,
                OnAccept = () => { _sound.Play(Sfx.Launch); return Task.CompletedTask; },
                OnAdjust = dir =>
                {
                    d.SoundVolume = Math.Clamp(d.SoundVolume + dir * 10, 0, 100);
                    _settings.Save();
                    _sound.Play(Sfx.Select);
                },
            },
        };
    }

    private void ToggleMusic(bool on)
    {
        var d = _settings.Data;
        d.MusicEnabled = on;
        _settings.Save();
        _music.Reload();
        if (on && BackgroundMusic.CountFiles() == 0)
            ShowToast("Legg lydfiler (mp3, wav, flac, m4a) i musikkmappen under Lyd");
    }

    // ---------- ZenTune (innebygd) ----------

    private async Task OpenTuneAsync(TileVm t)
    {
        if (t.Game is null) return;
        ShowToast("ZenTune leser spillet…");
        try
        {
            var d = await TuneBridge.FindAsync(t.Game);
            if (d is null) { ShowToast($"ZenTune kjenner ikke {t.Name} ennå."); return; }
            var tuner = TuneBridge.TunerFor(d);
            if (tuner is null) { ShowToast($"Ingen ferdige innstillinger for {t.Name} ennå."); return; }
            var hw = await TuneBridge.HardwareAsync();
            OpenMenu($"ZenTune: {d.Name}", () => BuildTuneMenu(d, tuner, hw));
        }
        catch (Exception ex)
        {
            ShowToast("ZenTune feilet: " + ex.Message);
        }
    }

    private List<MenuEntry> BuildTuneMenu(DetectedGame d, IGameTuner tuner, HardwareProfile hw)
    {
        var rec = PresetInfo.Recommended(hw);
        var list = new List<MenuEntry>();
        foreach (var kind in new[] { PresetKind.Competitive, PresetKind.Balanced, PresetKind.Quality, PresetKind.Ultra, PresetKind.Handheld })
        {
            var k = kind;
            string name = PresetInfo.Title(k) + (k == rec ? "  (anbefalt)" : "");
            list.Add(new MenuEntry(name, "se hva som endres")
            {
                OnAccept = () => OpenPresetAsync(d, tuner, k, hw),
            });
        }
        list.Add(new MenuEntry("Gjenopprett innstillinger", "tilbake til en backup")
        {
            OnAccept = () => { OpenMenu("Gjenopprett: " + d.Name, () => BuildRestoreMenu(d)); return Task.CompletedTask; },
        });
        return list;
    }

    private async Task OpenPresetAsync(DetectedGame d, IGameTuner tuner, PresetKind kind, HardwareProfile hw)
    {
        try
        {
            var plan = await TuneBridge.PlanAsync(d, tuner, kind, hw);
            OpenMenu($"{PresetInfo.Title(kind)}: {d.Name}", () => BuildPlanMenu(d, plan));
        }
        catch (Exception ex)
        {
            ShowToast("Kunne ikke lage forslag: " + ex.Message);
        }
    }

    private List<MenuEntry> BuildPlanMenu(DetectedGame d, PresetPlan plan)
    {
        var list = new List<MenuEntry>();
        var changes = plan.Effective.ToList();

        list.Add(new MenuEntry("Avbryt", plan.Summary)
        {
            OnAccept = () => { BackMenu(); return Task.CompletedTask; },
        });

        if (plan.GuideOnly)
        {
            list.Add(new MenuEntry("Kun veiledning", "ZenTune skriver ikke til dette spillet (anti-juks)"));
        }
        else if (changes.Count == 0)
        {
            list.Add(new MenuEntry("Ingen endringer", "innstillingene er allerede slik"));
        }
        else
        {
            list.Add(new MenuEntry("Bruk disse innstillingene", $"{changes.Count} endringer, backup tas først")
            {
                OnAccept = () => ApplyPlanAsync(d, plan),
            });
        }

        foreach (var c in plan.Changes.Take(40))
        {
            var change = c;
            string from = change.OldValue ?? "—";
            list.Add(new MenuEntry(change.Label, $"{from} → {change.NewValue}"));
        }
        return list;
    }

    private async Task ApplyPlanAsync(DetectedGame d, PresetPlan plan)
    {
        if (TuneBridge.IsRunning(d))
        {
            ShowToast($"Lukk {d.Name} først, så skriver ZenTune innstillingene.");
            return;
        }
        var prev = _mode;
        _mode = Mode.Busy;
        try
        {
            ShowToast("Tar backup og skriver innstillinger…");
            int n = await TuneBridge.ApplyAsync(plan);
            await TuneBridge.RefreshAsync(d);
            _mode = prev;
            CloseMenus();
            ShowToast($"Ferdig: {n} innstillinger endret i {d.Name}.");
        }
        catch (Exception ex)
        {
            _mode = prev;
            ShowToast("Feilet: " + ex.Message);
        }
    }

    private List<MenuEntry> BuildRestoreMenu(DetectedGame d)
    {
        var list = new List<MenuEntry>();
        var backups = TuneBridge.BackupsFor(d);
        if (backups.Count == 0)
        {
            list.Add(new MenuEntry("Ingen backuper ennå", "de lages automatisk før hver endring"));
            return list;
        }
        foreach (var b in backups.Take(15))
        {
            var backup = b;
            list.Add(new MenuEntry(backup.Created.ToString("d. MMM HH:mm"), backup.Label)
            {
                OnAccept = async () =>
                {
                    if (TuneBridge.IsRunning(d)) { ShowToast($"Lukk {d.Name} først."); return; }
                    try
                    {
                        await TuneBridge.RestoreAsync(d, backup);
                        await TuneBridge.RefreshAsync(d);
                        CloseMenus();
                        ShowToast("Innstillingene er gjenopprettet.");
                    }
                    catch (Exception ex) { ShowToast("Feilet: " + ex.Message); }
                },
            });
        }
        return list;
    }

    /// <summary>Ett steg tilbake i menyen (som B).</summary>
    private void BackMenu()
    {
        if (_menus.Count > 1) { _menus.Pop(); RebuildMenu(true); }
        else CloseMenus();
    }

    private List<MenuEntry> BuildPowerMenu()
    {
        MenuEntry Confirm(string title, string question, string yesText, string shutdownArgs) => new(title)
        {
            OnAccept = () =>
            {
                OpenMenu(question, () => new List<MenuEntry>
                {
                    // Avbryt først, så feiltrykk ikke slår av PC-en.
                    new("Avbryt") { OnAccept = () => { Back(); return Task.CompletedTask; } },
                    new(yesText) { OnAccept = () => { RunShutdown(shutdownArgs); return Task.CompletedTask; } },
                });
                return Task.CompletedTask;
            },
        };

        return new List<MenuEntry>
        {
            new("Hvilemodus")
            {
                OnAccept = () =>
                {
                    CloseMenus();
                    NativeMethods.SetSuspendState(false, false, false);
                    return Task.CompletedTask;
                },
            },
            Confirm("Start på nytt", "Start PC-en på nytt?", "Ja, start på nytt", "/r /t 0"),
            Confirm("Slå av", "Slå av PC-en?", "Ja, slå av", "/s /t 0"),
            Confirm("Logg ut", "Logge ut?", "Ja, logg ut", "/l"),
            new("Avslutt ZenLauncher", "til skrivebordet")
            {
                OnAccept = () => { Application.Current.Exit(); return Task.CompletedTask; },
            },
        };
    }

    private void RunShutdown(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            ShowToast("Kunne ikke utføre: " + ex.Message);
        }
    }

    private List<MenuEntry> BuildSystemMenu()
    {
        MenuEntry Link(string title, string target, string detail = "") => new(title, detail)
        {
            OnAccept = () => { OpenExternal(target); return Task.CompletedTask; },
        };

        return new List<MenuEntry>
        {
            Link("Windows-innstillinger", "ms-settings:"),
            Link("Skjerm", "ms-settings:display"),
            Link("Lyd", "ms-settings:sound"),
            Link("Bluetooth og enheter", "ms-settings:bluetooth"),
            Link("Nettverk og internett", "ms-settings:network"),
            Link("Game Bar", "ms-settings:gaming-gamebar"),
            Link("Xbox mode / spilling", "ms-settings:gaming-xboxmode"),
            Link("Windows Update", "ms-settings:windowsupdate"),
            Link("Oppgavebehandling", "taskmgr.exe"),
        };
    }

    private List<MenuEntry> BuildAddMenu()
    {
        var items = AppCatalog.Candidates(_settings.Data.Apps)
            .Select(c => new MenuEntry(c.Name, c.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "nettside" : "program")
            {
                OnAccept = () => AddAppAsync(c),
            })
            .ToList();
        items.Insert(0, new MenuEntry("Velg program (.exe)…", "bla gjennom filer") { OnAccept = PickExeAsync });
        var have = new HashSet<string>(_settings.Data.Apps.Select(a => a.Target), StringComparer.OrdinalIgnoreCase);
        var scanned = _installedApps;
        if (scanned is null)
        {
            _ = ScanInstalledAppsAsync();
            items.Add(new MenuEntry("Skanner installerte apper…", "åpne menyen på nytt om et øyeblikk")
            {
                KeepOpen = true,
                OnAccept = () => Task.CompletedTask,
            });
        }
        else
        {
            var known = new HashSet<string>(items.Select(i => i.Title), StringComparer.OrdinalIgnoreCase);
            foreach (var a in scanned)
            {
                if (have.Contains(a.Target) || known.Contains(a.Name)) continue;
                var app = a;
                items.Add(new MenuEntry(app.Name, app.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "Store-app" : "installert")
                {
                    OnAccept = () => AddAppAsync(app),
                });
            }
        }
        return items;
    }

    private List<AppEntry>? _installedApps;
    private bool _scanningApps;

    private async Task ScanInstalledAppsAsync()
    {
        if (_scanningApps) return;
        _scanningApps = true;
        try { _installedApps = await InstalledApps.ScanAsync(); }
        catch { _installedApps = new List<AppEntry>(); }
        finally { _scanningApps = false; }
    }

    private async Task AddAppAsync(AppEntry app)
    {
        await LoadIconAsync(app);
        _settings.Data.Apps.Add(app);
        _settings.Save();
        CloseMenus();
        _filter = FAll;
        _search = "";
        RebuildTiles($"app:{app.Target}");
        ShowToast($"{app.Name} er lagt til");
    }

    private async Task PickExeAsync()
    {
        _mode = Mode.Busy;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await AddAppAsync(new AppEntry(Path.GetFileNameWithoutExtension(file.Path), file.Path));
        }
        catch (Exception ex)
        {
            ShowToast("Kunne ikke åpne filvelger: " + ex.Message);
        }
        finally
        {
            if (_menus.Count > 0) _mode = Mode.Menu;
            else if (_mode == Mode.Busy) _mode = Mode.Home;
        }
    }

    // ---------- Start apper / åpne eksternt ----------

    private void OpenExternal(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            CloseMenus();
        }
        catch (Exception ex)
        {
            ShowToast("Kunne ikke åpne: " + ex.Message);
        }
    }

    private void LaunchApp(TileVm t)
    {
        if (t.Key.StartsWith("stream:", StringComparison.Ordinal)) { PlayStreamTile(t); return; }
        if (t.NotInstalled && t.Key.StartsWith("steam:", StringComparison.Ordinal))
        {
            _sound.Play(Sfx.Select);
            _ = StartSteamInstallAsync(t);
            return;
        }
        if (t.NotInstalled)
        {
            _sound.Play(Sfx.Select);
            OpenMenu($"Installer {t.Name} på…", () => BuildInstallDiskMenu(t));
            return;
        }
        var a = t.App!;
        _sound.Play(Sfx.Launch);
        try
        {
            var psi = new ProcessStartInfo(a.Target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(a.Args)) psi.Arguments = a.Args;
            if (File.Exists(a.Target)) psi.WorkingDirectory = Path.GetDirectoryName(a.Target)!;
            Process.Start(psi);
            if (t.Store is not null)
            {
                _settings.Data.LastLaunch[t.Key] = NowUnix();
                _settings.Save();
                _ = TrackStorePlayAsync(t);
            }
            ShowToast($"Starter {a.Name}…");
        }
        catch (Exception ex)
        {
            ShowToast($"Kunne ikke starte {a.Name}: {ex.Message}");
        }
    }

    private readonly HashSet<string> _tracking = new();

    /// <summary>
    /// Følger et Epic/GOG/Ubisoft-spill etter start: spillet regnes som i gang så lenge en prosess kjører fra
    /// installasjonsmappen. Spilletid lagres når den har vært borte en stund (launcher → spill kan bytte prosess).
    /// Økter under ti sekunder telles ikke, som for Steam.
    /// </summary>
    private async Task TrackStorePlayAsync(TileVm t)
    {
        var dir = t.InstallDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || !_tracking.Add(t.Key)) return;
        try
        {
            var prefix = dir.TrimEnd('\\', '/') + "\\";
            DateTime? first = null, last = null;
            int missing = 0;
            var waited = Stopwatch.StartNew();
            while (true)
            {
                bool alive = await Task.Run(() => NativeMethods.AnyProcessUnder(prefix));
                if (alive)
                {
                    if (first is null) { first = DateTime.UtcNow; _ = ApplyGameVolumeAsync(t); }
                    last = DateTime.UtcNow;
                    missing = 0;
                }
                else if (first is not null)
                {
                    if (++missing >= 3) break;   // borte i ca. 15 sekunder: spillet er avsluttet
                }
                else if (waited.Elapsed > TimeSpan.FromMinutes(3)) return; // spillet kom aldri opp

                await Task.Delay(first is null ? 3000 : 5000);
            }

            var secs = (long)(last!.Value - first!.Value).TotalSeconds;
            if (secs >= 10)
            {
                var d = _settings.Data;
                d.PlaySeconds[t.Key] = (d.PlaySeconds.TryGetValue(t.Key, out var old) ? old : 0) + secs;
                if (PluginOn("sessions")) RecordSession(t.Key, secs);
            }
            _settings.Data.LastLaunch[t.Key] = NowUnix();
            _settings.Save();
            RebuildTiles();
            RefreshMeta();
        }
        catch { }
        finally { _tracking.Remove(t.Key); }
    }

    private void ShowToast(string msg)
    {
        ToastText.Text = msg;
        ToastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------- Spillstart (Steam) ----------

    private async Task LaunchGameAsync(TileVm t)
    {
        if (t.Game is null) return;

        // Et spill kjører allerede: gå tilbake til det, eller be om å avslutte det først.
        if (_running is not null)
        {
            if (_running.Key == t.Key) ResumeGame();
            else ShowToast($"Avslutt «{_running.Name}» først.");
            return;
        }

        if (_mode == Mode.Busy) return;
        _mode = Mode.Busy;
        _sound.Play(Sfx.Launch);

        ShowCover(t);

        LaunchResult result;
        DateTime? startedAt = null;
        try
        {
            result = await _launcher.LaunchSteamGameAsync(
                t.Game,
                _hwnd,
                s => DispatcherQueue.TryEnqueue(() => CoverStatus.Text = s),
                () =>
                {
                    startedAt = DateTime.UtcNow;
                    DispatcherQueue.TryEnqueue(async () => await OnGameStartedAsync(t));
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            CoverStatus.Text = ex.Message;
            await Task.Delay(2500);
            result = LaunchResult.NeverStarted;
        }

        if (result == LaunchResult.NeverStarted)
        {
            CoverStatus.Text = "Spillet startet ikke. Sjekk Steam.";
            await Task.Delay(2000);
        }

        // Spilletid: bare økter vi så starte (og som varte over ti sekunder) telles.
        if (startedAt is { } st)
        {
            var secs = (long)(DateTime.UtcNow - st).TotalSeconds;
            if (secs >= 10)
            {
                var d = _settings.Data;
                d.PlaySeconds[t.Key] = (d.PlaySeconds.TryGetValue(t.Key, out var old) ? old : 0) + secs;
                if (PluginOn("sessions")) RecordSession(t.Key, secs);
            }
            _settings.Data.LastLaunch[t.Key] = NowUnix();
            _settings.Save();
        }

        // Spillet er ferdig (eller startet aldri): tilbake til hjemskjermen.
        _running = null;
        UpdateRunningBanner();
        NativeMethods.SetForegroundWindow(_hwnd);
        Activate();
        if (LaunchCover.Visibility == Visibility.Visible) await HideCoverAsync();
        if (_mode == Mode.Busy) _mode = Mode.Home;
        _ = RescanAsync(false); // fanger opp nye installasjoner og oppdatert «sist spilt»
        RebuildTiles();
        RefreshMeta();
        if (_mode == Mode.Home) GameList.Focus(FocusState.Programmatic);
        RefreshSelectionHint();
    }

    /// <summary>Spillet har startet: slipp oppstartsskjermen, og la ZenLauncher navigeres mens spillet kjører.</summary>
    private async Task OnGameStartedAsync(TileVm t)
    {
        _running = t;
        _ = ApplyGameVolumeAsync(t);
        UpdateRunningBanner();
        await HideCoverAsync();
        if (_mode == Mode.Busy) _mode = Mode.Home;
        RefreshSelectionHint();
    }

    private void ResumeGame()
    {
        if (_running?.Game is null) return;
        _sound.Play(Sfx.Launch);
        if (!GameLauncher.TryActivateGameWindow(_running.Game))
            NativeMethods.ShowWindow(_hwnd, 6); // SW_MINIMIZE: spillet ligger bak oss
    }

    private void UpdateRunningBanner()
    {
        RunningBanner.Visibility = _running is null ? Visibility.Collapsed : Visibility.Visible;
        if (_running is not null) RunningText.Text = $"▶ Kjører: {_running.Name}";
    }

    private void RefreshSelectionHint()
    {
        if (GameList.SelectedItem is TileVm t) HintText.Text = HintFor(t);
    }

    private string HintFor(TileVm t) =>
        _running is not null && _running.Key == t.Key ? "A: tilbake til spillet   ≡: meny"
        : t.Kind switch
        {
            TileKind.Game => "A: spill   Y: favoritt   ⧉: spillmeny   LB/RB: kategori   X: søk   ≡: meny",
            TileKind.App => "A: åpne   Y: favoritt   ⧉: spillmeny   LB/RB: kategori   X: søk   ≡: meny",
            _ => "A: legg til en nettleser, et program eller en nettside",
        };

    private async void ShowCover(TileVm t)
    {
        try { await t.EnsureHeroAsync(); } catch { }
        CoverHero.Source = t.Hero;
        CoverTitle.Text = t.Name;
        CoverStatus.Text = "Forbereder…";
        LaunchCover.Opacity = 0;
        LaunchCover.Visibility = Visibility.Visible;
        await Task.Delay(30);
        LaunchCover.Opacity = 1;
    }

    private async Task HideCoverAsync()
    {
        LaunchCover.Opacity = 0;
        await Task.Delay(400);
        LaunchCover.Visibility = Visibility.Collapsed;
    }
}
