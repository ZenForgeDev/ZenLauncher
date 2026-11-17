using System.Text.Json;
using Microsoft.Win32;

namespace ZenStationShell.Services;

/// <summary>Et installert spill fra Epic, GOG eller Ubisoft. Target er en exe eller en launcher-adresse.</summary>
public sealed record StoreGame(string Store, string Id, string Name, string Target, string? Args, string? IconExe, string? CoverUrl = null, string? InstallDir = null)
{
    public string Key => $"{Store.ToLowerInvariant()}:{Id}";
}

/// <summary>
/// Finner installerte spill fra andre butikker uten innlogging, ved å lese filer og register lokalt.
///  - Epic: manifestfiler i ProgramData (*.item, JSON). Startes via Epics launcher-adresse.
///  - GOG: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt; (gameName, path, exe). Startes direkte via exe.
///  - Ubisoft: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;id&gt; (InstallDir). Startes via uplay://launch/&lt;id&gt;/0.
/// Hver butikk skannes for seg, og en feil i én stopper ikke de andre.
/// </summary>
public static class OtherStores
{
    /// <summary>Target for Epic-spill som ligger i biblioteket men ikke er installert.</summary>
    public const string EpicInstallPrefix = "zl-epic-install:";
    /// <summary>Target for GOG-spill som ligger i biblioteket men ikke er installert.</summary>
    public const string GogInstallPrefix = "zl-gog-install:";
    /// <summary>Target for Steam-spill du eier men ikke har installert.</summary>
    public const string SteamInstallPrefix = "zl-steam-install:";

    /// <summary>True for plassholder-mål (spill i biblioteket som ikke er installert ennå).</summary>
    public static bool IsInstallPlaceholder(string? target) =>
        target is not null &&
        (target.StartsWith(EpicInstallPrefix, StringComparison.Ordinal) ||
         target.StartsWith(GogInstallPrefix, StringComparison.Ordinal) ||
         target.StartsWith(SteamInstallPrefix, StringComparison.Ordinal));

    public static List<StoreGame> Scan()
    {
        var all = new List<StoreGame>();
        try { all.AddRange(Epic()); } catch { }
        try { all.AddRange(EpicLegendary()); } catch { }
        try { all.AddRange(EpicNotInstalled()); } catch { }
        try { all.AddRange(Gog()); } catch { }
        try { all.AddRange(Ubisoft()); } catch { }
        try { all.AddRange(GogViaGogdl()); } catch { }
        try { all.AddRange(GogNotInstalled()); } catch { }
        try { all.AddRange(SteamNotInstalled()); } catch { }
        // Epic-omslag fra biblioteks-cachen, også for installerte spill.
        try
        {
            var covers = LegendaryService.LoadLibraryCache()
                .Where(c => !string.IsNullOrEmpty(c.CoverUrl))
                .GroupBy(c => c.AppName)
                .ToDictionary(c => c.Key, c => c.First().CoverUrl!);
            if (covers.Count > 0)
                all = all.Select(g => g.Store == "Epic" && g.CoverUrl is null && covers.TryGetValue(g.Id, out var url)
                    ? g with { CoverUrl = url } : g).ToList();
        }
        catch { }

        try
        {
            var gcov = GogService.LoadLibraryCache()
                .Where(c => !string.IsNullOrEmpty(c.CoverUrl))
                .GroupBy(c => c.Id)
                .ToDictionary(c => c.Key, c => c.First().CoverUrl!);
            if (gcov.Count > 0)
                all = all.Select(g => g.Store == "GOG" && g.CoverUrl is null && gcov.TryGetValue(g.Id, out var url)
                    ? g with { CoverUrl = url } : g).ToList();
        }
        catch { }

        return all
            .GroupBy(g => g.Key)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<StoreGame> Epic()
    {
        var list = new List<StoreGame>();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return list;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var r = doc.RootElement;
                string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

                var app = Str("AppName");
                var name = Str("DisplayName");
                var install = Str("InstallLocation");
                var exe = Str("LaunchExecutable");
                var main = Str("MainGameAppName");

                if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(name)) continue;
                if (string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(install)) continue; // uten start-exe: DLC/verktøy
                if (!string.IsNullOrEmpty(main) && !string.Equals(main, app, StringComparison.OrdinalIgnoreCase)) continue; // DLC
                if (Flag("bIsIncompleteInstall")) continue;
                if (app.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue; // Unreal Engine
                if (!Directory.Exists(install)) continue;

                list.Add(new StoreGame("Epic", app, name,
                    $"com.epicgames.launcher://apps/{app}?action=launch&silent=true", null,
                    Path.Combine(install, exe), InstallDir: install));
            }
            catch { /* hopp over ødelagt manifest */ }
        }
        return list;
    }

    /// <summary>
    /// Epic-spill installert via Legendary (uten Epic Games Launcher): leser installed.json i
    /// %USERPROFILE%\.config\legendary (eller mappen i LEGENDARY_CONFIG_PATH). Startes direkte via exe.
    /// </summary>
    private static List<StoreGame> EpicLegendary()
    {
        var list = new List<StoreGame>();
        var cfg = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(cfg))
            cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "legendary");
        var file = Path.Combine(cfg, "installed.json");
        if (!File.Exists(file)) return list;

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            try
            {
                var r = prop.Value;
                string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Flag(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

                var app = Str("app_name") ?? prop.Name;
                var title = Str("title");
                var install = Str("install_path");
                var exe = Str("executable");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(install) || string.IsNullOrWhiteSpace(exe)) continue;
                if (Flag("is_dlc")) continue;
                if (app.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue;

                var full = Path.GetFullPath(Path.Combine(install.Replace('/', '\\'), exe.Replace('/', '\\')));
                if (!File.Exists(full)) continue;
                var args = Str("launch_parameters");
                list.Add(new StoreGame("Epic", app, title, full,
                    string.IsNullOrWhiteSpace(args) ? null : args.Trim(), full,
                    InstallDir: Path.GetFullPath(install.Replace('/', '\\'))));
            }
            catch { }
        }
        return list;
    }

    /// <summary>True hvis Legendary selv har installert spillet (står i installed.json). Bare da kan Legendary avinstallere det.</summary>
    public static bool IsLegendaryInstalled(string appName)
    {
        try
        {
            var cfg = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
            if (string.IsNullOrWhiteSpace(cfg))
                cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "legendary");
            var file = Path.Combine(cfg, "installed.json");
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(appName, out _);
        }
        catch { return false; }
    }

    /// <summary>Epic-biblioteket fra siste Legendary-henting. Installerte spill vinner ved lik nøkkel (Scan fjerner dubletter).</summary>
    private static List<StoreGame> EpicNotInstalled() =>
        LegendaryService.LoadLibraryCache()
            .Select(g => new StoreGame("Epic", g.AppName, g.Title, EpicInstallPrefix + g.AppName, null, null, g.CoverUrl))
            .ToList();

    /// <summary>GOG-spill installert via gogdl (ikke Galaxy): liste i gog-installed.json, startfil fra goggame-&lt;id&gt;.info.</summary>
    private static List<StoreGame> GogViaGogdl()
    {
        var list = new List<StoreGame>();
        foreach (var g in GogService.LoadInstalled())
        {
            try
            {
                if (!Directory.Exists(g.InstallPath)) continue;
                var launch = GogService.FindLaunch(g.InstallPath, g.Id);
                if (launch is null) continue;
                list.Add(new StoreGame("GOG", g.Id, g.Title, launch.Value.Exe, launch.Value.Args, launch.Value.Exe, InstallDir: g.InstallPath));
            }
            catch { }
        }
        return list;
    }

    /// <summary>Eide Steam-spill (fra Web API-cachen) som ikke er installert. Installerte spill kommer fra SteamLibrary.Scan.</summary>
    private static List<StoreGame> SteamNotInstalled()
    {
        var owned = SteamOwned.LoadCache();
        if (owned.Count == 0) return new();
        var installed = SteamLibrary.Scan().Select(g => g.AppId).ToHashSet();
        return owned.Where(g => !installed.Contains(g.AppId))
            .Select(g => new StoreGame("Steam", g.AppId.ToString(), g.Name, SteamInstallPrefix + g.AppId, null, null,
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.AppId}/library_600x900.jpg"))
            .ToList();
    }

    private static List<StoreGame> GogNotInstalled() =>
        GogService.LoadLibraryCache()
            .Select(g => new StoreGame("GOG", g.Id, g.Title, GogInstallPrefix + g.Id, null, null, g.CoverUrl))
            .ToList();

    private static List<StoreGame> Gog()
    {
        var list = new List<StoreGame>();
        using var games = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (games is null) return list;

        foreach (var id in games.GetSubKeyNames())
        {
            try
            {
                using var k = games.OpenSubKey(id);
                var name = k?.GetValue("gameName") as string;
                var exe = k?.GetValue("exe") as string;
                var args = k?.GetValue("launchParam") as string;
                var gpath = k?.GetValue("path") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) continue;
                list.Add(new StoreGame("GOG", id, name, exe,
                    string.IsNullOrWhiteSpace(args) ? null : args.Trim(), exe,
                    InstallDir: string.IsNullOrWhiteSpace(gpath) ? Path.GetDirectoryName(exe) : gpath));
            }
            catch { }
        }
        return list;
    }

    private static List<StoreGame> Ubisoft()
    {
        var list = new List<StoreGame>();
        using var installs = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        if (installs is null) return list;

        foreach (var id in installs.GetSubKeyNames())
        {
            try
            {
                using var k = installs.OpenSubKey(id);
                var dir = (k?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                var name = Path.GetFileName(dir); // Ubisoft lagrer ikke visningsnavn i registeret
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new StoreGame("Ubisoft", id, name, $"uplay://launch/{id}/0", null, null, InstallDir: dir));
            }
            catch { }
        }
        return list;
    }
}
