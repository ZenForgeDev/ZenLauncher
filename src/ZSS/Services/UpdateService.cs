using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZenStationShell.Services;

/// <summary>
/// Ser etter ny versjon på GitHub: siste «release» i et offentlig repo (api.github.com). Installerer ingenting selv;
/// appen viser bare at en ny versjon finnes og åpner utgivelsessiden i nettleseren.
/// </summary>
public static class UpdateService
{
    private static readonly HttpClient Http = MakeHttp();
    private static HttpClient MakeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher-update-check");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public sealed record Result(bool Ok, string Latest, string Url, bool Newer, string Error, string AssetUrl = "", string AssetName = "", string AssetSha = "");

    /// <summary>Godtar «eier/navn» eller en hel GitHub-adresse. Returnerer tom streng hvis det ikke ser riktig ut.</summary>
    public static string NormalizeRepo(string s)
    {
        s = (s ?? "").Trim();
        s = Regex.Replace(s, @"^https?://(www\.)?github\.com/", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\.git$", "", RegexOptions.IgnoreCase).Trim('/');
        var m = Regex.Match(s, @"^([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)");
        return m.Success ? m.Groups[1].Value + "/" + m.Groups[2].Value : "";
    }

    private static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag ?? "", @"(\d+)(\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    public static async Task<Result> CheckAsync(string repo, string current)
    {
        repo = NormalizeRepo(repo);
        if (repo.Length == 0) return new(false, "", "", false, "Skriv inn GitHub-repoet (eier/navn) først.");
        try
        {
            using var r = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(false, "", "", false, "Fant ingen utgivelse (release) i " + repo + " ennå.");
            if (!r.IsSuccessStatusCode) return new(false, "", "", false, "GitHub svarte " + (int)r.StatusCode + ".");
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) url = $"https://github.com/{repo}/releases";
            var latest = ParseVersion(tag);
            var cur = ParseVersion(current);
            if (latest is null) return new(false, tag, url, false, "Forstod ikke versjonsnummeret «" + tag + "».");
            bool newer = cur is not null && latest > cur;
            var shown = Regex.Match(tag, @"\d+(\.\d+)+").Value;
            string aUrl = "", aName = "", aSha = "";
            if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var n = a.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                    if (!Regex.IsMatch(n, @"setup.*\.exe$", RegexOptions.IgnoreCase)) continue;
                    var au = a.TryGetProperty("browser_download_url", out var uu) ? uu.GetString() ?? "" : "";
                    if (!au.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) continue;
                    aUrl = au; aName = n;
                    var dg = a.TryGetProperty("digest", out var dd) ? dd.GetString() ?? "" : "";
                    if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) aSha = dg[7..].ToLowerInvariant();
                    break;
                }
            return new(true, shown, url, newer, "", aUrl, aName, aSha);
        }
        catch (Exception ex) { return new(false, "", "", false, "Kunne ikke sjekke: " + ex.Message); }
    }

    /// <summary>Laster ned installasjonsfilen til en egen temp-mappe. Sjekker SHA-256 når GitHub oppgir den. Returnerer stien, eller null med feilmelding.</summary>
    public static async Task<(string? Path, string Error)> DownloadInstallerAsync(string url, string name, string sha256, Action<string> report)
    {
        try
        {
            if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return (null, "Ugyldig nedlastingsadresse.");
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZenLauncherUpdate");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(name));
            using var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher-update-check");
            using var resp = await c.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920]; long done = 0; var last = DateTime.MinValue; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 500)
                    {
                        last = DateTime.UtcNow;
                        report(total > 0 ? $"Laster ned oppdatering: {done / 1048576.0:0.#} av {total / 1048576.0:0.#} MB" : $"Laster ned oppdatering: {done / 1048576.0:0.#} MB");
                    }
                }
            }
            if (sha256.Length > 0)
            {
                using var fs = File.OpenRead(path);
                var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (hash != sha256) { try { File.Delete(path); } catch { } return (null, "Sjekksummen stemte ikke. Filen ble slettet."); }
            }
            return (path, "");
        }
        catch (Exception ex) { return (null, "Nedlasting feilet: " + ex.Message); }
    }
}
