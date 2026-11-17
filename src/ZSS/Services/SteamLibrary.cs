using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZenStationShell.Services;

/// <summary>Finner installerte Steam-spill ved å lese Steams egne filer (ingen innlogging/nettverk).</summary>
public static class SteamLibrary
{
    private static readonly Regex KeyValue =
        new("^\\s*\"([^\"]+)\"\\s+\"([^\"]*)\"\\s*$", RegexOptions.Compiled);

    private static readonly string[] CoverNames = { "library_600x900.jpg", "library_capsule.jpg" };
    private static readonly string[] HeroNames = { "library_hero.jpg" };

    public static string? GetSteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
    }

    public static string? GetSteamExe()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var exe = (key?.GetValue("SteamExe") as string)?.Replace('/', '\\');
        if (!string.IsNullOrEmpty(exe) && File.Exists(exe)) return exe;

        var path = GetSteamPath();
        return path is null ? null : Path.Combine(path, "steam.exe");
    }

    public static List<GameEntry> Scan()
    {
        var result = new List<GameEntry>();
        var steam = GetSteamPath();
        if (steam is null || !Directory.Exists(steam)) return result;

        var libraries = Libraries(steam);

        var seen = new HashSet<int>();
        foreach (var lib in libraries)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;

            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var entry = ParseManifest(acf, steam);
                if (entry is not null && seen.Add(entry.AppId))
                    result.Add(entry);
            }
        }

        return result
            .OrderByDescending(g => g.LastPlayed)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Alle bibliotekmapper (Steam kan ha spill på flere disker).</summary>
    public static List<string> Libraries(string steam)
    {
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (var line in File.ReadLines(vdf))
            {
                var m = KeyValue.Match(line);
                if (m.Success && m.Groups[1].Value == "path")
                {
                    var p = m.Groups[2].Value.Replace(@"\\", @"\");
                    if (!libraries.Contains(p, StringComparer.OrdinalIgnoreCase))
                        libraries.Add(p);
                }
            }
        }
        return libraries;
    }

    private static long InstalledAt(string acfPath)
    {
        try { return new DateTimeOffset(File.GetCreationTimeUtc(acfPath)).ToUnixTimeSeconds(); }
        catch { return 0; }
    }

    private static GameEntry? ParseManifest(string acfPath, string steamPath)
    {
        try
        {
            // Første forekomst av hver nøkkel er toppnivået i AppState.
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(acfPath))
            {
                var m = KeyValue.Match(line);
                if (m.Success) kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            }

            if (!kv.TryGetValue("appid", out var idStr) || !int.TryParse(idStr, out var appId)) return null;
            if (!kv.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)) return null;

            // StateFlags: bit 4 = fullt installert.
            if (kv.TryGetValue("StateFlags", out var sf) && int.TryParse(sf, out var flags) && (flags & 4) == 0)
                return null;

            if (IsTool(name)) return null;

            kv.TryGetValue("installdir", out var installDir);
            long lastPlayed = kv.TryGetValue("LastPlayed", out var lp) && long.TryParse(lp, out var l) ? l : 0;

            return new GameEntry(
                appId,
                name,
                installDir ?? "",
                lastPlayed,
                FindCached(steamPath, appId, CoverNames),
                FindCached(steamPath, appId, HeroNames),
                InstalledAt(acfPath),
                string.IsNullOrEmpty(installDir) ? null : Path.Combine(Path.GetDirectoryName(acfPath)!, "common", installDir));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTool(string name) =>
        name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Controller", StringComparison.OrdinalIgnoreCase);

    /// <summary>Steams lokale bildecache har skiftet struktur mellom versjoner, så vi prøver begge.</summary>
    private static string? FindCached(string steamPath, int appId, string[] fileNames)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return null;

        // Eldre struktur: librarycache\{appid}_library_600x900.jpg
        foreach (var n in fileNames)
        {
            var old = Path.Combine(cache, $"{appId}_{n}");
            if (File.Exists(old)) return old;
        }

        // Nyere struktur: librarycache\{appid}\...\library_600x900.jpg
        var appDir = Path.Combine(cache, appId.ToString());
        if (Directory.Exists(appDir))
        {
            foreach (var n in fileNames)
            {
                var found = Directory.EnumerateFiles(appDir, n, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found;
            }
        }

        return null; // ImageLoader faller tilbake til Steams CDN
    }
}
