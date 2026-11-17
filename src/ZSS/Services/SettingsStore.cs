using System.Text.Json;

namespace ZenStationShell.Services;

public sealed class SettingsData
{
    public bool SoundEnabled { get; set; } = true;
    public int SoundVolume { get; set; } = 70; // 0-100
    public bool PadMouse { get; set; } = false;
    public List<AppEntry> Apps { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    /// <summary>Spilletid i sekunder per flis-nøkkel. Telles for Steam, Epic, GOG og Ubisoft når spillet startes fra ZenLauncher.</summary>
    public Dictionary<string, long> PlaySeconds { get; set; } = new();
    /// <summary>Sist startet fra ZenStation (unix-sekunder) per flis-nøkkel.</summary>
    public Dictionary<string, long> LastLaunch { get; set; } = new();
    /// <summary>Sortering på hjemskjermen: 0 standard, 1 A–Å, 2 nylig installert, 3 mest spilt.</summary>
    public int SortMode { get; set; } = 0;
    /// <summary>Desktop-modus: true = rutenett med plakater, false = liste med detaljer.</summary>
    public bool DesktopGrid { get; set; } = false;
    /// <summary>Plugins (valgfrie tillegg) per id. Alle er AV som standard.</summary>
    public Dictionary<string, bool> Plugins { get; set; } = new();
    /// <summary>Plugin «Spilleøkter»: [start (unix), varighet (sekunder)] per flis-nøkkel, siste 30.</summary>
    public Dictionary<string, List<long[]>> Sessions { get; set; } = new();
    /// <summary>Plugin «Notater»: egne notater per flis-nøkkel.</summary>
    public Dictionary<string, string> Notes { get; set; } = new();
    /// <summary>Bakgrunnsmusikk fra egen mappe. Av som standard.</summary>
    public bool MusicEnabled { get; set; } = false;
    public int MusicVolume { get; set; } = 30;
    /// <summary>Heltebildet flytter seg litt når du blar. Billig (kjører på GPU).</summary>
    public bool Parallax { get; set; } = true;
    /// <summary>Ramme rundt valgt flis: 0 regnbue (animert), 1-6 enkeltfarge, 7 av. Enkeltfarge og av koster ingen CPU.</summary>
    public int FrameTheme { get; set; } = 0;
    /// <summary>Minutter uten aktivitet før skjermen dimmes. 0 = av (standard).</summary>
    public int DimMinutes { get; set; } = 0;
    /// <summary>Minutter uten aktivitet før PC-en settes i hvilemodus. 0 = av (standard).</summary>
    public int SleepMinutes { get; set; } = 0;
    /// <summary>Spillvolum (0-100) per flis-nøkkel. Mangler nøkkelen, røres ikke spillets volum.</summary>
    public Dictionary<string, int> GameVolume { get; set; } = new();
    /// <summary>Skjulte spill (flis-nøkler). Vises bare i Skjult-fanen, og ellers ingen steder.</summary>
    public List<string> Hidden { get; set; } = new();
    /// <summary>Vis Skjult-fanen i kategoriraden. Av som standard.</summary>
    public bool ShowHiddenTab { get; set; } = false;
    /// <summary>Desktop-modus: vanlig vindu (mus og tastatur) i stedet for fullskjerm-konsollvisning.</summary>
    public bool DesktopMode { get; set; } = false;
    /// <summary>Brukerens egen Steam Web API-nøkkel (for listen over eide, uinstallerte spill). Personlig, aldri i logg.</summary>
    public string SteamApiKey { get; set; } = "";
    /// <summary>Egne samlinger: navn → flis-nøkler.</summary>
    public Dictionary<string, List<string>> Collections { get; set; } = new();
    /// <summary>Streaming: 0 av, 1 klient (Moonlight), 2 vert (Apollo).</summary>
    public int StreamMode { get; set; } = 0;
    /// <summary>Kjente verter som «navn|adresse».</summary>
    public List<string> StreamHosts { get; set; } = new();
    public int StreamRes { get; set; } = 1;      // 0 720p, 1 1080p, 2 1440p, 3 4K
    public int StreamFps { get; set; } = 1;      // 0 30, 1 60, 2 120
    public int StreamBitrate { get; set; } = 1;  // 0 10, 1 20, 2 40, 3 80 Mbit/s
    public bool StreamQuitAfter { get; set; } = false;
    public bool StreamVirtual { get; set; } = true;
    public bool StreamHostScreensOff { get; set; } = false;
    /// <summary>Apollo-tjenesten er satt til å starte med Windows (gjøres én gang med ett UAC-spørsmål).</summary>
    public bool ApolloAutostartDone { get; set; } = false;
    /// <summary>Oppstart: start med Windows, via Oppgaveplanlegger (rask), start skjult i systemkurven, lukk til systemkurven.</summary>
    public bool StartWithWindows { get; set; } = false;
    public bool FastStartup { get; set; } = false;
    public bool StartInTray { get; set; } = false;
    public bool CloseToTray { get; set; } = false;
    /// <summary>Innlogging til Apollos nettgrensesnitt (kun localhost). Lages av ZenLauncher ved oppsett.</summary>
    public string ApolloUser { get; set; } = "";
    public string ApolloPass { get; set; } = "";
    /// <summary>Oppdateringssjekk mot GitHub: repo («eier/navn»), av/på, og siste versjon vi har varslet om.</summary>
    public const string DefaultUpdateRepo = "ZenForgeDev/ZenLauncher";
    private string _updateRepo = DefaultUpdateRepo;
    /// <summary>GitHub-repoet oppdateringssjekken bruker. Tom verdi i settings.json gir standardrepoet.</summary>
    public string UpdateRepo { get => string.IsNullOrWhiteSpace(_updateRepo) ? DefaultUpdateRepo : _updateRepo; set => _updateRepo = value ?? ""; }
    public bool CheckUpdates { get; set; } = true;
    public string UpdateSeen { get; set; } = "";
    /// <summary>Sikkerhetskopi av innstillinger (uten passord/nøkler): automatisk høyst én gang i døgnet. LastBackup = unix-sekunder.</summary>
    public bool AutoBackup { get; set; } = true;
    public long LastBackup { get; set; } = 0;
    /// <summary>Siste versjon som kjørte, brukt til «Hva er nytt» etter oppdatering.</summary>
    public string LastRunVersion { get; set; } = "";
    /// <summary>Wake-on-LAN: MAC-adresse per vertsadresse, og om verten vekkes automatisk når du starter en strøm.</summary>
    public Dictionary<string, string> WolMacs { get; set; } = new();
    public bool StreamAutoWake { get; set; } = true;
    /// <summary>Begrens strømmen til 1080p og 20 Mbit/s når verten er nådd via Tailscale (100.x.x.x). Av som standard.</summary>
    public bool StreamAutoQuality { get; set; } = false;
    /// <summary>Sist startede strøm, for «Fortsett sist».</summary>
    public string LastStreamAddr { get; set; } = "";
    public string LastStreamApp { get; set; } = "";
}

/// <summary>Enkel JSON-lagring av innstillinger, apper og favoritter.</summary>
public sealed class SettingsStore
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "settings.json");

    public SettingsData Data { get; private set; } = new();

    public static SettingsStore Load()
    {
        var s = new SettingsStore();
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath));
                if (d is not null) s.Data = d;
            }
        }
        catch { /* ødelagt fil: start med standard */ }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ikke kritisk */ }
    }

    public bool IsFavorite(string key) => Data.Favorites.Contains(key);

    public bool ToggleFavorite(string key)
    {
        bool now = !Data.Favorites.Remove(key);
        if (now) Data.Favorites.Add(key);
        Save();
        return now;
    }
}
