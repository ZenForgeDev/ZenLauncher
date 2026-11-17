using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ZenStationShell.Plugins;

/// <summary>Tid for å fullføre et spill (timer). 0 = ukjent.</summary>
public sealed record HltbData(string Name, double Main, double Extra, double Complete);

/// <summary>
/// Plugin: HowLongToBeat. EKSPERIMENTELL og UOFFISIELL: howlongtobeat.com har ikke noe åpent API, og nettsiden endrer
/// søkefunksjonen sin jevnlig, så dette kan slutte å virke uten varsel. Alt feiler stille. Er av som standard.
/// Sender bare spillnavnet til howlongtobeat.com. Svar (også «ingen treff») lagres lokalt.
/// </summary>
public static class HowLongToBeat
{
    public const string Id = "hltb";
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, HltbData?> Mem = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "plugins", "hltb");

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://howlongtobeat.com/");
        h.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://howlongtobeat.com");
        return h;
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static async Task<HltbData?> GetAsync(string gameName)
    {
        string key = Norm(gameName);
        if (key.Length == 0) return null;
        if (Mem.TryGetValue(key, out var hit)) return hit;
        HltbData? result = null;
        string file = Path.Combine(Dir, (key.Length > 80 ? key[..80] : key) + ".json");
        try
        {
            if (File.Exists(file))
            {
                var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalDays;
                using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                bool found = cached.RootElement.GetProperty("found").GetBoolean();
                if (found && age < 60)
                    return Mem[key] = new HltbData(cached.RootElement.GetProperty("name").GetString() ?? gameName,
                        cached.RootElement.GetProperty("main").GetDouble(), cached.RootElement.GetProperty("extra").GetDouble(),
                        cached.RootElement.GetProperty("complete").GetDouble());
                if (!found && age < 3) return Mem[key] = null; // ikke prøv på nytt hele tiden
            }
        }
        catch { }

        bool reached = false;
        try
        {
            result = await SearchAsync(gameName, key);
            reached = true;
        }
        catch { }

        if (reached)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var o = result is null
                    ? new { found = false, name = "", main = 0.0, extra = 0.0, complete = 0.0 }
                    : new { found = true, name = result.Name, main = result.Main, extra = result.Extra, complete = result.Complete };
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(o));
            }
            catch { }
        }
        return Mem[key] = result;
    }

    private static readonly string[] Bases = { "/api/search/site", "/api/find", "/api/bleed", "/api/search" };
    private static string? _goodBase;

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var f = Path.Combine(Dir, "hltb.log");
            if (File.Exists(f) && new FileInfo(f).Length > 100_000) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private static object Any() => new { mode = "include", values = Array.Empty<string>() };

    private static async Task<HltbData?> SearchAsync(string gameName, string key)
    {
        // Nettsiden har flyttet søket flere ganger. Prøv sist kjente sti først, så de andre.
        var order = new List<string>();
        if (_goodBase is not null) order.Add(_goodBase);
        order.AddRange(Bases.Where(b => b != _goodBase));

        Exception? last = null;
        foreach (var b in order)
        {
            try
            {
                var r = await SearchOnAsync(b, gameName, key);
                _goodBase = b;
                return r;
            }
            catch (HltbPathMissing) { continue; }
            catch (Exception ex) { last = ex; Log($"{b}: {ex.Message}"); _goodBase = null; if (ex is HttpRequestException) continue; throw; }
        }
        throw last ?? new HttpRequestException("HLTB: ingen søkesti virket");
    }

    private sealed class HltbPathMissing : Exception { }

    private static async Task<HltbData?> SearchOnAsync(string basePath, string gameName, string key)
    {
        // 1) Hent en kortlevd søkenøkkel (nettsidens egen JavaScript gjør det samme). hpKey/hpVal er valgfrie.
        string token = "", hpKey = "", hpVal = "";
        using (var initResp = await Http.GetAsync("https://howlongtobeat.com" + basePath + "/init?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
        {
            string body = await initResp.Content.ReadAsStringAsync();
            Log($"{basePath}/init -> {(int)initResp.StatusCode} {Short(body)}");
            if ((int)initResp.StatusCode is 404 or 405) throw new HltbPathMissing();
            initResp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("token", out var t)) token = t.GetString() ?? "";
            if (r.TryGetProperty("hpKey", out var k)) hpKey = k.GetString() ?? "";
            if (r.TryGetProperty("hpVal", out var v)) hpVal = v.GetString() ?? "";
        }
        if (token.Length == 0) throw new InvalidDataException("HLTB: fikk ingen token");

        // 2) Søk. Filtrene bruker { mode, values } etter nettsidens redesign.
        var payload = new Dictionary<string, object?>
        {
            ["searchType"] = "games",
            ["searchTerms"] = gameName.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ["searchPage"] = 1,
            ["size"] = 20,
            ["searchOptions"] = new Dictionary<string, object?>
            {
                ["games"] = new Dictionary<string, object?>
                {
                    ["userId"] = 0,
                    ["platform"] = Any(),
                    ["sortCategory"] = "popular",
                    ["rangeCategory"] = "main",
                    ["rangeTime"] = new { min = (int?)null, max = (int?)null },
                    ["gameplay"] = new { perspective = Any(), flow = Any(), genre = Any() },
                    ["year"] = Any(),
                    ["modifier"] = "",
                },
                ["users"] = new { sortCategory = "postcount" },
                ["lists"] = new { sortCategory = "follows" },
                ["filter"] = "",
                ["sort"] = 0,
                ["randomizer"] = 0,
            },
            ["useCache"] = true,
        };
        if (hpKey.Length > 0 && hpVal.Length > 0) payload[hpKey] = hpVal;

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://howlongtobeat.com" + basePath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-auth-token", token);
        if (hpKey.Length > 0 && hpVal.Length > 0) { req.Headers.TryAddWithoutValidation("x-hp-key", hpKey); req.Headers.TryAddWithoutValidation("x-hp-val", hpVal); }

        using var resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        Log($"{basePath} søk «{gameName}» -> {(int)resp.StatusCode} {Short(text)}");
        if ((int)resp.StatusCode is 404 or 405) throw new HltbPathMissing();
        resp.EnsureSuccessStatusCode();

        using var res = JsonDocument.Parse(text);
        if (!res.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("HLTB: uventet svar");

        HltbData? best = null;
        foreach (var g in data.EnumerateArray())
        {
            string name = g.TryGetProperty("game_name", out var n) ? n.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            string nn = Norm(name);
            bool exact = nn == key;
            if (!exact && best is not null) continue;
            if (!exact && !(nn.Contains(key) || key.Contains(nn))) continue;
            // Tidene kommer vanligvis i sekunder; små tall tolkes som timer.
            double H(string f)
            {
                if (!g.TryGetProperty(f, out var p) || !p.TryGetDouble(out var d) || d <= 0) return 0;
                return d > 1000 ? d / 3600.0 : d;
            }
            var item = new HltbData(name, H("comp_main"), H("comp_plus"), H("comp_100"));
            if (exact) return item;
            best = item;
        }
        return best;
    }
}
