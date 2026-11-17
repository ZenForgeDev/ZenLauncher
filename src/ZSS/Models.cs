using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ZenStationShell.Services;

namespace ZenStationShell;

/// <summary>Et installert Steam-spill (data uten UI-avhengigheter).</summary>
public sealed record GameEntry(
    int AppId,
    string Name,
    string InstallDir,
    long LastPlayed,
    string? CoverPath,
    string? HeroPath,
    long Installed = 0,
    string? InstallPath = null)
{
    public string CoverFallbackUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/library_600x900.jpg";
    public string HeroFallbackUrl => $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/library_hero.jpg";
}

/// <summary>Et lagt-til program: exe-sti eller nettadresse.</summary>
public sealed record AppEntry(string Name, string Target, string? Args = null);

public enum TileKind { Game, App, Add }

/// <summary>Én flis på hjemskjermen (spill, app eller «legg til»).</summary>
public sealed class TileVm : System.ComponentModel.INotifyPropertyChanged
{
    public TileKind Kind { get; init; }
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Glyph { get; set; } = "";
    public GameEntry? Game { get; private set; }
    public void UpdateGame(GameEntry g) => Game = g;
    public AppEntry? App { get; init; }
    /// <summary>Satt for spill fra Epic/GOG/Ubisoft (da kan de ikke «fjernes» som egne apper).</summary>
    public string? Store { get; init; }
    /// <summary>Installasjonsmappen til Epic/GOG/Ubisoft-spill. Brukes til å følge spillprosessen (spilletid).</summary>
    public string? InstallDir { get; init; }
    public ImageSource? Icon { get; set; }
    private ImageSource? _cover;
    private string? _coverUrl;
    /// <summary>Omslag. For butikkspill fra nett lages bildet først når flisen vises, så hundrevis av uinstallerte Steam-spill ikke fyller minnet.</summary>
    public ImageSource? Cover
    {
        get
        {
            if (_cover is null && _coverUrl is not null)
                _cover = new BitmapImage(new Uri(_coverUrl)) { DecodePixelWidth = 320 };
            return _cover;
        }
        private set => _cover = value;
    }
    public ImageSource? Hero { get; private set; }
    private string _star = "";
    public string Star
    {
        get => _star;
        set { if (_star == value) return; _star = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Star))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Butikken flisen hører til (Steam, Epic, GOG, Ubisoft). Tom for egne apper.</summary>
    public string StoreName { get; init; } = "";
    /// <summary>Epic-spill i biblioteket som ikke er installert ennå.</summary>
    public bool NotInstalled => OtherStores.IsInstallPlaceholder(App?.Target);
    public string StoreLabel => StoreName.ToUpperInvariant();
    public Visibility StoreVisibility => StoreName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush StoreBrush => new SolidColorBrush(StoreName switch
    {
        "Steam" => Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x1B, 0x28, 0x38),
        "Epic" => Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x2A, 0x2A, 0x2A),
        "GOG" => Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x86, 0x1F, 0xB3),
        "Ubisoft" => Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x0A, 0x6C, 0xC4),
        _ => Microsoft.UI.ColorHelper.FromArgb(0xE6, 0x33, 0x33, 0x33)
    });
    /// <summary>Liten merkelapp oppe til venstre, f.eks. «FORTSETT».</summary>
    public string Badge { get; set; } = "";
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public long LastPlayed => Game?.LastPlayed ?? 0;
    public long Installed => Game?.Installed ?? 0;

    public static TileVm FromGame(GameEntry g) => new()
    {
        Kind = TileKind.Game, Key = $"steam:{g.AppId}", Name = g.Name, StoreName = "Steam",
        Glyph = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?", Game = g
    };

    public static TileVm FromApp(AppEntry a) => new()
    {
        Kind = TileKind.App, Key = $"app:{a.Target}", Name = a.Name,
        Glyph = a.Name.Length > 0 ? a.Name[..1].ToUpperInvariant() : "?", App = a
    };

    public static TileVm FromStore(StoreGame g) => new()
    {
        Kind = TileKind.App, Key = g.Key, Name = g.Name, Store = g.Store, StoreName = g.Store, InstallDir = g.InstallDir,
        Glyph = g.Name.Length > 0 ? g.Name[..1].ToUpperInvariant() : "?",
        App = new AppEntry(g.Name, g.Target, g.Args),
        _coverUrl = string.IsNullOrEmpty(g.CoverUrl) ? null : g.CoverUrl
    };

    /// <summary>Gratis-fanen: åpner siden i nettleseren. Store er null, så det telles ikke som spilt spill.</summary>
    public static TileVm FromFree(FreeGame g) => new()
    {
        Kind = TileKind.App, Key = "free:" + g.Id, Name = g.Title, StoreName = g.Store,
        Badge = g.Until.Length > 0 ? ("GRATIS " + g.Until).ToUpperInvariant() : "GRATIS",
        Glyph = g.Title.Length > 0 ? g.Title[..1].ToUpperInvariant() : "?",
        App = new AppEntry(g.Title, g.Url),
        _coverUrl = string.IsNullOrEmpty(g.Image) ? null : g.Image
    };

    public static TileVm AddTile() => new() { Kind = TileKind.Add, Key = "add", Name = "Legg til app", Glyph = "+" };

    /// <summary>Laster bare omslaget (lite). Heltebildet er stort og lastes først når flisen er valgt.</summary>
    public async Task LoadImagesAsync()
    {
        if (Game is null) return;
        Cover = await ImageLoader.LoadAsync(Game.CoverPath, Game.CoverFallbackUrl, 320);
    }

    public async Task EnsureHeroAsync()
    {
        if (Game is null || Hero is not null) return;
        Hero = await ImageLoader.LoadAsync(Game.HeroPath, Game.HeroFallbackUrl, 1920);
    }

    /// <summary>Slipper det store heltebildet (frigjør minne) når flisen ikke lenger er valgt.</summary>
    public void ReleaseHero() => Hero = null;
}

/// <summary>Rad i meny. OnAdjust kalles med -1/+1 for venstre/høyre.</summary>
public sealed class MenuEntry
{
    public MenuEntry(string title, string detail = "") { Title = title; Detail = detail; }
    public string Title { get; }
    public string Detail { get; set; }
    public Func<Task>? OnAccept { get; init; }
    public Action<int>? OnAdjust { get; init; }
    public bool KeepOpen { get; init; }
    /// <summary>Desktop: ◄ ► vises bare når raden ikke kan byttes med et enkelt klikk.</summary>
    public Visibility AdjustVisibility => OnAdjust is not null && (!Compact || OnAccept is null) ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Desktop: uten pilene i teksten (et klikk bytter verdi).</summary>
    public string DetailText => Compact ? Detail.Replace("◄ ", "").Replace(" ►", "") : Detail;
    public double DetailMaxWidth { get; } = Compact ? 320 : 240;
    /// <summary>Desktop-modus: kompakte rader (mindre tekst og luft) i stedet for konsollstørrelse.</summary>
    public static bool Compact { get; set; }
    public double TitleSize { get; } = Compact ? 15 : 22;
    public double DetailSize { get; } = Compact ? 13 : 18;
    public Thickness RowPadding { get; } = Compact ? new Thickness(14, 9, 14, 9) : new Thickness(16, 14, 16, 14);
}
