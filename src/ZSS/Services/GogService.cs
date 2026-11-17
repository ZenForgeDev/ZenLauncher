using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ZenStationShell.Services;

public sealed record GogLibraryGame(string Id, string Title, string? CoverUrl);
public sealed record GogInstalledGame(string Id, string Title, string InstallPath);

/// <summary>
/// GOG via heroic-gogdl (github.com/Heroic-Games-Launcher/heroic-gogdl, Windows-exe fra siste release).
/// gogdl kan logge inn (auth --code), laste ned/oppdatere (download) og vise info, men har INGEN kommando for å liste
/// biblioteket. Biblioteket hentes derfor fra GOGs eget API med tokenet gogdl lagrer i gog-auth.json.
/// Antakelser som må testes mot ekte konto: tokenfilens format, bibliotek-endepunktet og utskriftsformatet under nedlasting.
/// </summary>
public static class GogService
{
    public const string LoginUrl =
        "https://auth.gog.com/auth?client_id=46899977096215655&redirect_uri=https%3A%2F%2Fembed.gog.com%2Fon_login_success%3Forigin%3Dclient&response_type=code&layout=galaxy";

    private const string LatestReleaseApi = "https://api.github.com/repos/Heroic-Games-Launcher/heroic-gogdl/releases/latest";

    public static string ToolsDir => LegendaryService.ToolsDir;
    public static string ExePath => Path.Combine(ToolsDir, "gogdl.exe");
    public static string AuthPath => Path.Combine(ToolsDir, "gog-auth.json");
    public static string LibraryCachePath => Path.Combine(ToolsDir, "gog-library.json");
    public static string InstalledPath => Path.Combine(ToolsDir, "gog-installed.json");

    public static bool IsAvailable => File.Exists(ExePath);
    public static bool HasToken => ReadToken() is not null;
    public static string LastError { get; private set; } = "";

    private static void Remember(string what, int code, string output, string error)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            File.AppendAllText(Path.Combine(ToolsDir, "gogdl.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what} -> exit {code}\n--- stdout ---\n{output}\n--- stderr ---\n{error}\n\n");
        }
        catch { }
        var last = (error + "\n" + output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        LastError = last.Length > 140 ? last[..140] : last;
    }

    public static async Task<bool> EnsureToolAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(ExePath)) return true;
        try
        {
            Directory.CreateDirectory(ToolsDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher/1.0");
            var json = await http.GetStringAsync(LatestReleaseApi, ct);
            using var doc = JsonDocument.Parse(json);
            var wanted = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                         System.Runtime.InteropServices.Architecture.Arm64
                ? "gogdl_windows_arm64.exe" : "gogdl_windows_x86_64.exe";
            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString() == wanted) { url = a.GetProperty("browser_download_url").GetString(); break; }
            if (string.IsNullOrEmpty(url)) return File.Exists(ExePath);
            var tmp = ExePath + ".download";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(tmp).Length < 1_000_000) { File.Delete(tmp); return File.Exists(ExePath); }
            File.Move(tmp, ExePath, true);
            return true;
        }
        catch (Exception ex) { LastError = ex.Message; return File.Exists(ExePath); }
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(string args, int timeoutSeconds, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(ExePath, $"--auth-config-path \"{AuthPath}\" {args}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte gogdl.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var o = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var e = proc.StandardError.ReadToEndAsync(cts.Token);
        try { await proc.WaitForExitAsync(cts.Token); return (proc.ExitCode, await o, await e); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
    }

    /// <summary>Logger inn med koden fra adressen GOG sender deg til etter innlogging (…on_login_success?code=KODE).</summary>
    public static async Task<bool> TryAuthWithCodeAsync(string code, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var clean = new string(code.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (clean.Length == 0) return false;
        try
        {
            var (exit, o, e) = await RunAsync($"auth --code {clean}", 60, ct);
            Remember("auth --code", exit, o, e);
            return exit == 0;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    private static string? FindAccessToken(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.Name == "access_token" && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
                var inner = FindAccessToken(p.Value);
                if (inner is not null) return inner;
            }
        }
        return null;
    }

    private static string? ReadToken()
    {
        try
        {
            if (!File.Exists(AuthPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthPath));
            return FindAccessToken(doc.RootElement);
        }
        catch { return null; }
    }

    /// <summary>Ber gogdl fornye tokenet (kommandoen «auth» uten kode).</summary>
    private static async Task RefreshTokenAsync(CancellationToken ct)
    {
        try
        {
            var (exit, o, e) = await RunAsync("auth", 60, ct);
            Remember("auth (refresh)", exit, o, e);
        }
        catch { }
    }

    public static async Task<List<GogLibraryGame>> ListLibraryAsync(CancellationToken ct = default)
    {
        var list = new List<GogLibraryGame>();
        if (!IsAvailable) return list;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher/1.0");
            bool refreshed = false;
            int page = 1, totalPages = 1;
            while (page <= totalPages && page <= 50)
            {
                var token = ReadToken();
                if (token is null && !refreshed) { await RefreshTokenAsync(ct); refreshed = true; token = ReadToken(); }
                if (token is null) { LastError = "Fant ikke innloggingstoken (logg inn først)"; return new(); }

                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await http.SendAsync(req, ct);
                if ((int)resp.StatusCode is 401 or 403 && !refreshed)
                {
                    await RefreshTokenAsync(ct);
                    refreshed = true;
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct);
                Remember($"library page {page}", (int)resp.StatusCode, text.Length > 1500 ? text[..1500] : text, "");
                if (!resp.IsSuccessStatusCode) { LastError = $"GOG svarte {(int)resp.StatusCode}"; return new(); }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var tpv)) totalPages = tpv;
                if (!root.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) break;
                foreach (var p in products.EnumerateArray())
                {
                    var id = p.TryGetProperty("id", out var i) ? i.ToString() : null;
                    var title = p.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var image = p.TryGetProperty("image", out var im) ? im.GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
                    string? cover = null;
                    if (!string.IsNullOrEmpty(image))
                        cover = (image.StartsWith("//") ? "https:" + image : image) + "_product_card_v2_mobile_slider_639.jpg";
                    list.Add(new GogLibraryGame(id, title, cover));
                }
                page++;
            }
        }
        catch (Exception ex) { LastError = ex.Message; }

        var sorted = list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (sorted.Count > 0)
        {
            try { File.WriteAllText(LibraryCachePath, JsonSerializer.Serialize(sorted)); } catch { }
        }
        return sorted;
    }

    public static List<GogLibraryGame> LoadLibraryCache()
    {
        try
        {
            return File.Exists(LibraryCachePath)
                ? JsonSerializer.Deserialize<List<GogLibraryGame>>(File.ReadAllText(LibraryCachePath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public static List<GogInstalledGame> LoadInstalled()
    {
        try
        {
            return File.Exists(InstalledPath)
                ? JsonSerializer.Deserialize<List<GogInstalledGame>>(File.ReadAllText(InstalledPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static void SaveInstalled(List<GogInstalledGame> list)
    {
        try { Directory.CreateDirectory(ToolsDir); File.WriteAllText(InstalledPath, JsonSerializer.Serialize(list)); }
        catch { }
    }

    /// <summary>gogdl lagrer et manifest i %APPDATA%\heroic_gogdl\manifests\&lt;id&gt; og svarer «Nothing to do» hvis det er likt GOGs, uten å se på disken.
    /// Slettes mappen må manifestet også bort, ellers blir en ny installasjon tom.</summary>
    private static void ForgetManifest(string id)
    {
        try
        {
            if (id.Length == 0 || !id.All(char.IsDigit)) return;
            var cfg = Environment.GetEnvironmentVariable("GOGDL_CONFIG_PATH");
            var baseDir = !string.IsNullOrEmpty(cfg) ? cfg : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var file = Path.Combine(baseDir, "heroic_gogdl", "manifests", id);
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
    }

    public static bool IsInstalledByUs(string id) => LoadInstalled().Any(g => g.Id == id);

    /// <summary>Sletter spillmappen og fjerner spillet fra gog-installed.json. Nekter å slette rot- eller toppnivåmapper.</summary>
    public static (bool ok, string message) Uninstall(string id)
    {
        var list = LoadInstalled();
        var g = list.FirstOrDefault(x => x.Id == id);
        if (g is null) return (false, "Fant ikke spillet i listen over installerte GOG-spill");
        try
        {
            var path = Path.GetFullPath(g.InstallPath).TrimEnd('\\', '/');
            var root = Path.GetPathRoot(path)?.TrimEnd('\\', '/') ?? "";
            if (path.Length == 0 || string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length < 2)
                return (false, "Nekter å slette en så overordnet mappe: " + path);
            if (Directory.Exists(path)) Directory.Delete(path, true);
            ForgetManifest(id);
            list.RemoveAll(x => x.Id == id);
            SaveInstalled(list);
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string SafeFolder(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(title.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "Game" : s;
    }

    /// <summary>Finner startfilen ut fra goggame-&lt;id&gt;.info i installasjonsmappen (primær playTask).</summary>
    public static (string Exe, string? Args)? FindLaunch(string installPath, string id)
    {
        try
        {
            var info = Directory.EnumerateFiles(installPath, $"goggame-{id}.info", SearchOption.AllDirectories).FirstOrDefault();
            if (info is null) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(info));
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;
            var baseDir = Path.GetDirectoryName(info)!;
            JsonElement? pick = null;
            foreach (var t in tasks.EnumerateArray())
            {
                if (t.TryGetProperty("type", out var ty) && ty.GetString() != "FileTask") continue;
                if (!t.TryGetProperty("path", out _)) continue;
                if (pick is null) pick = t;
                if (t.TryGetProperty("isPrimary", out var pr) && pr.ValueKind == JsonValueKind.True) { pick = t; break; }
            }
            if (pick is null) return null;
            var rel = pick.Value.GetProperty("path").GetString();
            if (string.IsNullOrEmpty(rel)) return null;
            var exe = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('/', '\\')));
            var args = pick.Value.TryGetProperty("arguments", out var a) ? a.GetString() : null;
            return File.Exists(exe) ? (exe, string.IsNullOrWhiteSpace(args) ? null : args.Trim()) : null;
        }
        catch { return null; }
    }

    /// <summary>Laster ned et GOG-spill til basePath\&lt;spillnavn&gt;. Ved suksess lagres det i gog-installed.json.</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(
        string id, string title, string basePath, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!IsAvailable) return (false, "gogdl.exe mangler");
        if (id.Length == 0 || !id.All(char.IsDigit)) return (false, "ugyldig spill-ID");
        var target = Path.Combine(basePath, SafeFolder(title));
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { return (false, ex.Message); }
        // Finnes ikke spillet på disk (slettet, eller avbrutt tidligere), må gogdl glemme manifestet og laste alt på nytt.
        if (FindLaunch(target, id) is null) ForgetManifest(id);

        var psi = new ProcessStartInfo(ExePath,
            $"--auth-config-path \"{AuthPath}\" download {id} --path \"{target}\" --platform windows")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte gogdl.");
        var log = new System.Text.StringBuilder();
        var last = "";
        void Handle(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (log) { log.AppendLine(line); last = line.Trim(); }
            onLine?.Invoke(line.Trim());
        }
        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
        proc.WaitForExit();
        string text; lock (log) text = log.ToString();
        Remember($"download {id}", proc.ExitCode, text.Length > 6000 ? text[^6000..] : text, "");
        if (proc.ExitCode != 0) return (false, last);

        if (FindLaunch(target, id) is null) return (false, "Ferdig, men fant ikke startfilen (goggame-" + id + ".info)");
        var installed = LoadInstalled();
        installed.RemoveAll(g => g.Id == id);
        installed.Add(new GogInstalledGame(id, title, target));
        SaveInstalled(installed);
        return (true, last);
    }
}
