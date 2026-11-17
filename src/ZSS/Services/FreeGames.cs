using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace ZenStationShell.Services;

/// <summary>Et gratis spill akkurat nå. Url åpnes i nettleseren (der brukeren legger det til i kontoen sin).</summary>
public sealed record FreeGame(string Id, string Title, string Store, string Url, string? Image, string Until);

/// <summary>
/// Gratisspill fra to kilder: Epics offentlige freeGamesPromotions (ukens gratisspill) og GamerPower (Steam, GOG, Ubisoft m.fl.).
/// GamerPower krever synlig lenke til GamerPower.com når dataene vises (se AttributionUrl), og maks 10 forespørsler i sekundet.
/// Resultatet bufres i tools\free-games.json og hentes på nytt først etter noen timer.
/// </summary>
public static class FreeGames
{
    public const string AttributionUrl = "https://www.gamerpower.com";
    private const string EpicUrl = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=en-US&country=NO&allowCountries=NO";
    private const string GamerPowerUrl = "https://www.gamerpower.com/api/giveaways?platform=pc&type=game";

    public static string CachePath => Path.Combine(LegendaryService.ToolsDir, "free-games.json");
    public static string LastError { get; private set; } = "";

    public static List<FreeGame> LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return new();
            return JsonSerializer.Deserialize<List<FreeGame>>(File.ReadAllText(CachePath)) ?? new();
        }
        catch { return new(); }
    }

    public static bool CacheStale(int hours = 6)
    {
        try { return !File.Exists(CachePath) || DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) > TimeSpan.FromHours(hours); }
        catch { return true; }
    }

    /// <summary>Henter begge kildene. Feiler den ene, brukes den andre. Bufferen overskrives bare når vi fikk noe.</summary>
    public static async Task<List<FreeGame>> RefreshAsync(CancellationToken ct = default)
    {
        LastError = "";
        var result = new List<FreeGame>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher/1.0");

        try { result.AddRange(ParseEpic(await http.GetStringAsync(EpicUrl, ct))); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LastError = "Epic: " + ex.GetType().Name; }

        try { result.AddRange(ParseGamerPower(await http.GetStringAsync(GamerPowerUrl, ct))); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LastError += (LastError.Length > 0 ? "; " : "") + "GamerPower: " + ex.GetType().Name; }

        if (result.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(LegendaryService.ToolsDir);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(result));
            }
            catch { }
        }
        return result;
    }

    private static string UntilText(string? endIso)
    {
        if (string.IsNullOrEmpty(endIso)) return "";
        if (!DateTime.TryParse(endIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)) return "";
        return "til " + end.ToLocalTime().ToString("d. MMM", CultureInfo.GetCultureInfo("nb-NO"));
    }

    /// <summary>Epic: data.Catalog.searchStore.elements. Gratis nå = en aktiv kampanje med 0 % betaling og pris 0.</summary>
    private static List<FreeGame> ParseEpic(string json)
    {
        var list = new List<FreeGame>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Catalog", out var cat)
            || !cat.TryGetProperty("searchStore", out var ss)
            || !ss.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in els.EnumerateArray())
        {
            try
            {
                var title = e.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var id = e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                if (title.Length == 0 || id.Length == 0) continue;

                // Aktiv kampanje der du betaler 0 %.
                string? end = null;
                bool free = false;
                if (e.TryGetProperty("promotions", out var pr) && pr.ValueKind == JsonValueKind.Object
                    && pr.TryGetProperty("promotionalOffers", out var outer) && outer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var o in outer.EnumerateArray())
                    {
                        if (!o.TryGetProperty("promotionalOffers", out var inner) || inner.ValueKind != JsonValueKind.Array) continue;
                        foreach (var p in inner.EnumerateArray())
                        {
                            if (p.TryGetProperty("discountSetting", out var ds) && ds.TryGetProperty("discountPercentage", out var dp)
                                && dp.ValueKind == JsonValueKind.Number && dp.GetInt32() == 0)
                            {
                                free = true;
                                if (p.TryGetProperty("endDate", out var ed)) end = ed.GetString();
                            }
                        }
                    }
                }
                if (!free) continue;

                string? slug = null;
                if (e.TryGetProperty("productSlug", out var ps) && ps.ValueKind == JsonValueKind.String) slug = ps.GetString();
                if (string.IsNullOrEmpty(slug) && e.TryGetProperty("urlSlug", out var us) && us.ValueKind == JsonValueKind.String) slug = us.GetString();
                if (string.IsNullOrEmpty(slug) && e.TryGetProperty("offerMappings", out var om) && om.ValueKind == JsonValueKind.Array)
                    foreach (var m in om.EnumerateArray())
                        if (m.TryGetProperty("pageSlug", out var pg) && pg.ValueKind == JsonValueKind.String) { slug = pg.GetString(); break; }
                if (string.IsNullOrEmpty(slug)) continue;
                slug = slug.Split('/')[0];
                // Slugger kan ha «/home» på slutten; bare selve navnet brukes i adressen.

                string? image = null, any = null;
                if (e.TryGetProperty("keyImages", out var ki) && ki.ValueKind == JsonValueKind.Array)
                    foreach (var k in ki.EnumerateArray())
                    {
                        var ty = k.TryGetProperty("type", out var tt) ? tt.GetString() : null;
                        var u = k.TryGetProperty("url", out var uu) ? uu.GetString() : null;
                        if (string.IsNullOrEmpty(u)) continue;
                        any ??= u;
                        if (ty == "OfferImageTall") { image = u; break; }
                        if (ty == "Thumbnail" && image is null) image = u;
                    }

                list.Add(new FreeGame("epic-" + id, title, "Epic", $"https://store.epicgames.com/en-US/p/{slug}", image ?? any, UntilText(end)));
            }
            catch { }
        }
        return list;
    }

    /// <summary>GamerPower: en JSON-liste. Felter leses forsiktig siden de ikke er kontrollert mot dokumentasjonen.</summary>
    private static List<FreeGame> ParseGamerPower(string json)
    {
        var list = new List<FreeGame>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list; // f.eks. «ingen aktive» gir et objekt

        string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            try
            {
                var title = S(e, "title");
                if (title.Length == 0) continue;
                var status = S(e, "status");
                if (status.Length > 0 && !status.Equals("Active", StringComparison.OrdinalIgnoreCase)) continue;
                var platforms = S(e, "platforms");
                if (platforms.Contains("Epic", StringComparison.OrdinalIgnoreCase)) continue; // Epic hentes direkte

                var id = e.TryGetProperty("id", out var idv) ? idv.ToString() : title;
                var url = S(e, "open_giveaway_url");
                if (url.Length == 0) url = S(e, "gamerpower_url");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                var image = S(e, "image");
                if (image.Length == 0) image = S(e, "thumbnail");
                var store = platforms.Contains("Steam", StringComparison.OrdinalIgnoreCase) ? "Steam"
                    : platforms.Contains("GOG", StringComparison.OrdinalIgnoreCase) ? "GOG"
                    : platforms.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase) ? "Ubisoft"
                    : platforms.Contains("itch", StringComparison.OrdinalIgnoreCase) ? "itch.io"
                    : "PC";
                var end = S(e, "end_date");
                // «N/A» betyr uten sluttdato.
                var until = end.Length > 0 && !end.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? UntilText(end.Replace(' ', 'T')) : "";

                list.Add(new FreeGame("gp-" + id, title, store, url, image.Length > 0 ? image : null, until));
                if (list.Count >= 40) break;
            }
            catch { }
        }
        return list;
    }
}
