using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ZenStationShell.Services;

/// <summary>Beskrivelse, sjanger, utvikler og skjermbilder for et Steam-spill (Steams åpne butikk-API, ingen nøkkel).</summary>
public sealed record StoreInfoData(string Description, List<string> Genres, string Developer, string Released, List<string> Shots, int Metacritic,
    List<string> Categories, int Achievements);

/// <summary>Anmeldelser (tekst) og Steam Deck-status (0 ukjent, 1 ikke støttet, 2 spillbar, 3 verifisert).</summary>
public sealed record StoreExtras(string Reviews, int Deck);

public static class StoreInfo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<int, StoreInfoData?> Cache = new();
    private static readonly Dictionary<string, long> Sizes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "storecache");

    public static async Task<StoreInfoData?> GetAsync(int appId)
    {
        if (Cache.TryGetValue(appId, out var hit)) return hit;
        StoreInfoData? result = null;
        try
        {
            string file = Path.Combine(Dir, appId + ".json");
            string? json = null;
            if (File.Exists(file) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalDays < 30)
                json = await File.ReadAllTextAsync(file);
            if (json is null)
            {
                json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&l=norwegian");
                try { Directory.CreateDirectory(Dir); await File.WriteAllTextAsync(file, json); } catch { }
            }
            result = Parse(json, appId);
        }
        catch { }
        Cache[appId] = result;
        return result;
    }

    private static StoreInfoData? Parse(string json, int appId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId.ToString(), out var root)) return null;
        if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return null;
        var d = root.GetProperty("data");

        string desc = d.TryGetProperty("short_description", out var sd) ? WebUtility.HtmlDecode(sd.GetString() ?? "") : "";
        var genres = new List<string>();
        if (d.TryGetProperty("genres", out var gs) && gs.ValueKind == JsonValueKind.Array)
            foreach (var g in gs.EnumerateArray())
                if (g.TryGetProperty("description", out var gd) && gd.GetString() is { Length: > 0 } s) genres.Add(WebUtility.HtmlDecode(s));
        string dev = "";
        if (d.TryGetProperty("developers", out var ds) && ds.ValueKind == JsonValueKind.Array && ds.GetArrayLength() > 0)
            dev = WebUtility.HtmlDecode(ds[0].GetString() ?? "");
        string rel = d.TryGetProperty("release_date", out var rd) && rd.TryGetProperty("date", out var rdd) ? rdd.GetString() ?? "" : "";
        var shots = new List<string>();
        if (d.TryGetProperty("screenshots", out var ss) && ss.ValueKind == JsonValueKind.Array)
            foreach (var s in ss.EnumerateArray())
            {
                if (shots.Count >= 8) break;
                if (s.TryGetProperty("path_thumbnail", out var pt) && pt.GetString() is { Length: > 0 } u) shots.Add(u);
            }
        int meta = d.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var sc) && sc.TryGetInt32(out var v) ? v : 0;
        var cats = new List<string>();
        if (d.TryGetProperty("categories", out var cs) && cs.ValueKind == JsonValueKind.Array)
            foreach (var c in cs.EnumerateArray())
            {
                if (cats.Count >= 6) break;
                if (c.TryGetProperty("description", out var cd) && cd.GetString() is { Length: > 0 } t) cats.Add(WebUtility.HtmlDecode(t));
            }
        int ach = d.TryGetProperty("achievements", out var ac) && ac.TryGetProperty("total", out var at) && at.TryGetInt32(out var av) ? av : 0;
        return new StoreInfoData(desc, genres, dev, rel, shots, meta, cats, ach);
    }

    private static readonly Dictionary<int, StoreExtras> ExtraCache = new();

    /// <summary>Anmeldelser og Steam Deck-status (åpne API-er uten nøkkel). Feiler stille.</summary>
    public static async Task<StoreExtras> ExtrasAsync(int appId)
    {
        if (ExtraCache.TryGetValue(appId, out var hit)) return hit;
        string reviews = "";
        int deck = 0;
        var rt = Task.Run(async () =>
        {
            try
            {
                var j = await Http.GetStringAsync($"https://store.steampowered.com/appreviews/{appId}?json=1&language=all&purchase_type=all&num_per_page=0");
                using var doc = JsonDocument.Parse(j);
                var q = doc.RootElement.GetProperty("query_summary");
                int pos = q.GetProperty("total_positive").GetInt32(), neg = q.GetProperty("total_negative").GetInt32();
                int tot = pos + neg;
                if (tot < 10) return tot == 0 ? "" : "For få anmeldelser";
                int pct = (int)Math.Round(100.0 * pos / tot);
                string label = pct >= 95 ? "Overveldende positive" : pct >= 80 ? "Veldig positive" : pct >= 70 ? "Stort sett positive"
                    : pct >= 40 ? "Blandede" : pct >= 20 ? "Stort sett negative" : "Veldig negative";
                return $"{label} ({pct} %, {tot:N0} anm.)";
            }
            catch { return ""; }
        });
        var dt = Task.Run(async () =>
        {
            try
            {
                var j = await Http.GetStringAsync($"https://store.steampowered.com/saleaction/ajaxgetdeckappcompatibilityreport?nAppID={appId}");
                using var doc = JsonDocument.Parse(j);
                return doc.RootElement.GetProperty("results").GetProperty("resolved_category").GetInt32();
            }
            catch { return 0; }
        });
        reviews = await rt;
        deck = await dt;
        var res = new StoreExtras(reviews, deck);
        ExtraCache[appId] = res;
        return res;
    }

    /// <summary>Mappestørrelse i byte (beregnes i bakgrunnen og huskes). 0 hvis mappen ikke finnes.</summary>
    public static Task<long> FolderSizeAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult(0L);
        if (Sizes.TryGetValue(path, out var known)) return Task.FromResult(known);
        return Task.Run(() =>
        {
            long total = 0;
            try
            {
                if (Directory.Exists(path))
                    foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
            }
            catch { }
            lock (Sizes) Sizes[path] = total;
            return total;
        });
    }
}
