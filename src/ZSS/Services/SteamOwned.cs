using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZenStationShell.Services;

public sealed record SteamOwnedGame(int AppId, string Name);

/// <summary>
/// Steam-spill du eier men ikke har installert. Steam har ingen lokal liste over dette, så listen hentes fra Steams Web API
/// (IPlayerService/GetOwnedGames) med brukerens egen API-nøkkel. Nøkkelen er personlig og må ikke deles (Valves vilkår),
/// så den lagres bare lokalt i settings.json og skrives aldri til logg. Spilldetaljer på Steam-profilen må være offentlige.
/// Steam-ID-en leses fra Steams config\loginusers.vdf.
/// </summary>
public static class SteamOwned
{
    private static readonly Regex KeyValue = new("^\\s*\"([^\"]+)\"\\s+\"([^\"]*)\"\\s*$", RegexOptions.Compiled);

    public static string CachePath => Path.Combine(LegendaryService.ToolsDir, "steam-library.json");
    public static string LastError { get; private set; } = "";
    public const string KeyPageUrl = "https://steamcommunity.com/dev/apikey";

    /// <summary>SteamID64 til kontoen som sist logget inn: mostrecent=1 hvis den finnes, ellers høyest timestamp.</summary>
    public static string? GetSteamId64()
    {
        try
        {
            var steam = SteamLibrary.GetSteamPath();
            if (steam is null) return null;
            var file = Path.Combine(steam, "config", "loginusers.vdf");
            if (!File.Exists(file)) return null;

            string? current = null, best = null;
            long bestTs = -1;
            bool recent = false;
            long ts = 0;
            int depth = 0;
            void Close()
            {
                if (current is null) return;
                if (recent) { best = current; bestTs = long.MaxValue; }
                else if (ts > bestTs) { best = current; bestTs = ts; }
                current = null; recent = false; ts = 0;
            }
            string? pendingName = null;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line == "{") { depth++; if (depth == 2) { current = pendingName; recent = false; ts = 0; } continue; }
                if (line == "}") { if (depth == 2) Close(); depth--; continue; }
                var m = KeyValue.Match(raw);
                if (m.Success)
                {
                    if (depth == 2 && current is not null)
                    {
                        var k = m.Groups[1].Value;
                        if (k.Equals("mostrecent", StringComparison.OrdinalIgnoreCase)) recent = m.Groups[2].Value == "1";
                        else if (k.Equals("timestamp", StringComparison.OrdinalIgnoreCase)) long.TryParse(m.Groups[2].Value, out ts);
                    }
                    continue;
                }
                var n = Regex.Match(line, "^\"([0-9]{17})\"$");
                if (n.Success) pendingName = n.Groups[1].Value;
            }
            return best;
        }
        catch { return null; }
    }

    private static bool IsTool(string name) =>
        name.StartsWith("Steamworks Common", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Controller", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("SDK", StringComparison.OrdinalIgnoreCase);

    public static List<SteamOwnedGame> LoadCache()
    {
        try
        {
            return File.Exists(CachePath)
                ? JsonSerializer.Deserialize<List<SteamOwnedGame>>(File.ReadAllText(CachePath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static bool CacheStale() =>
        !File.Exists(CachePath) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath)).TotalHours > 12;

    /// <summary>Henter eide spill og lagrer dem i cachen. Tom liste ved feil (se LastError).</summary>
    public static async Task<List<SteamOwnedGame>> ListLibraryAsync(string apiKey, CancellationToken ct = default)
    {
        apiKey = (apiKey ?? "").Trim();
        if (apiKey.Length == 0) { LastError = "ingen API-nøkkel er lagt inn"; return new(); }
        var id = GetSteamId64();
        if (id is null) { LastError = "fant ikke Steam-kontoen (loginusers.vdf)"; return new(); }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher/1.0");
            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}" +
                      $"&steamid={id}&include_appinfo=1&include_played_free_games=1&format=json";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastError = (int)resp.StatusCode switch
                {
                    401 or 403 => "Steam avviste nøkkelen (feil eller tilbakekalt nøkkel?)",
                    429 => "for mange forespørsler, prøv igjen senere",
                    _ => $"Steam svarte {(int)resp.StatusCode}"
                };
                return new();
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
            {
                LastError = "Steam ga ingen spill. Sjekk at «Spilldetaljer» på profilen er offentlig";
                return new();
            }
            var list = new List<SteamOwnedGame>();
            foreach (var g in games.EnumerateArray())
            {
                if (!g.TryGetProperty("appid", out var a) || !a.TryGetInt32(out var appId)) continue;
                var name = g.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || IsTool(name)) continue;
                list.Add(new SteamOwnedGame(appId, name));
            }
            Directory.CreateDirectory(LegendaryService.ToolsDir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(list));
            LastError = "";
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name; // aldri ex.Message: kan inneholde adressen med nøkkelen
            return new();
        }
    }

    /// <summary>
    /// Fremdrift for et Steam-spill. Steam oppdaterer IKKE BytesDownloaded/BytesStaged i appmanifest_&lt;id&gt;.acf mens nedlastingen går
    /// (de står på 0 til slutt), så vi måler heller størrelsen på steamapps\downloading\&lt;id&gt;, der filene vokser etter hvert,
    /// og sammenligner med BytesToStage (ellers BytesToDownload). Null hvis manifestet ikke finnes ennå.
    /// </summary>
    public static (int Flags, long Done, long Total)? ReadProgress(int appId)
    {
        try
        {
            var steam = SteamLibrary.GetSteamPath();
            if (steam is null) return null;
            foreach (var lib in SteamLibrary.Libraries(steam))
            {
                var apps = Path.Combine(lib, "steamapps");
                var acf = Path.Combine(apps, $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var fs = new FileStream(acf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string? line;
                    while ((line = sr.ReadLine()) is not null)
                    {
                        var m = KeyValue.Match(line);
                        if (m.Success) kv.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                    }
                }
                int.TryParse(kv.GetValueOrDefault("StateFlags"), out var flags);
                long.TryParse(kv.GetValueOrDefault("BytesDownloaded"), out var dl);
                long.TryParse(kv.GetValueOrDefault("BytesToDownload"), out var toDl);
                long.TryParse(kv.GetValueOrDefault("BytesStaged"), out var staged);
                long.TryParse(kv.GetValueOrDefault("BytesToStage"), out var toStage);

                long onDisk = 0;
                var dir = Path.Combine(apps, "downloading", appId.ToString());
                if (Directory.Exists(dir))
                {
                    try
                    {
                        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                        {
                            try { onDisk += f.Length; } catch { }
                        }
                    }
                    catch { }
                }

                long total = toStage > 0 ? toStage : toDl;
                long done = toStage > 0 ? Math.Max(staged, onDisk) : Math.Max(dl, onDisk);
                if (total > 0 && done > total) done = total;
                return (flags, done, total);
            }
        }
        catch { }
        return null;
    }
}
