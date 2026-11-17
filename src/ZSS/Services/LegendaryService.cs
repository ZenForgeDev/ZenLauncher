using System.Diagnostics;
using System.Text.Json;

namespace ZenStationShell.Services;

/// <summary>Et spill i Epic-biblioteket (også ikke-installerte), slik Legendary lister det.</summary>
public sealed record EpicLibraryGame(string AppName, string Title, string? CoverUrl = null);

/// <summary>
/// Tynn innpakning rundt Legendary (CLI). Ikke koblet til UI ennå.
/// Legendary letes opp i %LOCALAPPDATA%\ZenStationShell\tools\legendary.exe, deretter i PATH.
/// Selve verktøyet lastes ned ved første bruk fra github.com/legendary-gl/legendary (siste release,
/// filen legendary_windows_x64.exe / _arm64.exe, verifisert mot 0.21.1) og kan oppdateres uten ny ZenLauncher-versjon.
/// Antakelser som må testes mot ekte Legendary: format på «list --json» og «status --json».
/// </summary>
public static class LegendaryService
{
    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "tools");

    public static string? FindExe()
    {
        var local = Path.Combine(ToolsDir, "legendary.exe");
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "legendary.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static bool IsAvailable => FindExe() is not null;

    /// <summary>Er det lagret en innlogging? (Legendary lagrer den i user.json; filen leses ikke.)</summary>
    public static bool IsLoggedIn
    {
        get
        {
            try
            {
                var cfg = Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH");
                if (string.IsNullOrWhiteSpace(cfg))
                    cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "legendary");
                return File.Exists(Path.Combine(cfg, "user.json"));
            }
            catch { return false; }
        }
    }

    /// <summary>Siste feilmelding fra Legendary (siste linje), til visning i toast. Hele utskriften ligger i legendary.log.</summary>
    public static string LastError { get; private set; } = "";

    private static void Remember(string what, int code, string output, string error)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            File.AppendAllText(Path.Combine(ToolsDir, "legendary.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what} -> exit {code}\n--- stdout ---\n{output}\n--- stderr ---\n{error}\n\n");
        }
        catch { }
        var last = (error + "\n" + output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";
        LastError = last.Length > 140 ? last[..140] : last;
    }

    /// <summary>Velger stående omslag hvis det finnes, ellers nærmeste alternativ.</summary>
    private static string? PickCover(JsonElement images)
    {
        foreach (var wanted in new[] { "DieselGameBoxTall", "OfferImageTall", "Thumbnail", "DieselGameBox", "OfferImageWide" })
        {
            foreach (var img in images.EnumerateArray())
            {
                if (img.ValueKind != JsonValueKind.Object) continue;
                var type = img.TryGetProperty("type", out var t) ? t.GetString() : null;
                var url = img.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (type == wanted && !string.IsNullOrWhiteSpace(url) &&
                    url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
        }
        return null;
    }

    public static string LibraryCachePath => Path.Combine(ToolsDir, "epic-library.json");

    private static void SaveLibraryCache(List<EpicLibraryGame> list)
    {
        try
        {
            Directory.CreateDirectory(ToolsDir);
            File.WriteAllText(LibraryCachePath, JsonSerializer.Serialize(list));
        }
        catch { }
    }

    /// <summary>Sist hentede Epic-bibliotek (leses uten nettverk og uten å starte Legendary).</summary>
    public static List<EpicLibraryGame> LoadLibraryCache()
    {
        try
        {
            if (!File.Exists(LibraryCachePath)) return new();
            return JsonSerializer.Deserialize<List<EpicLibraryGame>>(File.ReadAllText(LibraryCachePath)) ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Installerer et Epic-spill til basePath (Legendary lager spillmappen inni). onLine får hver utskriftslinje.</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(
        string appName, string basePath, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var exe = FindExe();
        if (exe is null) return (false, "legendary.exe mangler");
        if (appName.Length == 0 || !appName.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            return (false, "ugyldig spillnavn");
        try { Directory.CreateDirectory(basePath); }
        catch (Exception ex) { return (false, ex.Message); }

        for (var attempt = 0; ; attempt++)
        {
            var psi = new ProcessStartInfo(exe, $"-y install {appName} --base-path \"{basePath}\" --skip-sdl")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte Legendary.");
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
            Remember($"install {appName}", proc.ExitCode, text, "");
            // Legendary avslutter med kode 0 selv om den nektet å starte. «CRITICAL» i utskriften betyr feil.
            string textAll; lock (log) textAll = log.ToString();
            if (textAll.Contains("Failed to acquire installed data lock", StringComparison.OrdinalIgnoreCase))
            {
                // En annen Legendary-prosess holder låsen (f.eks. en nedlasting som avsluttes). Vent og prøv igjen.
                if (attempt < 60) { await Task.Delay(5000, ct); continue; }
                return (false, "Legendary er opptatt med en annen installasjon (låsen ble ikke ledig)");
            }
            if (proc.ExitCode == 0 && textAll.Contains("[cli] CRITICAL", StringComparison.Ordinal)) return (false, last);
            return (proc.ExitCode == 0, last);
        }
    }

    private const string LatestReleaseApi = "https://api.github.com/repos/legendary-gl/legendary/releases/latest";

    /// <summary>Laster ned (eller oppdaterer med force) legendary.exe til verktøymappen. True hvis den finnes etterpå.</summary>
    public static async Task<bool> EnsureToolAsync(bool force = false, CancellationToken ct = default)
    {
        var target = Path.Combine(ToolsDir, "legendary.exe");
        if (!force && File.Exists(target)) return true;
        try
        {
            Directory.CreateDirectory(ToolsDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher/1.0");
            var json = await http.GetStringAsync(LatestReleaseApi, ct);
            using var doc = JsonDocument.Parse(json);
            var wanted = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                         System.Runtime.InteropServices.Architecture.Arm64
                ? "legendary_windows_arm64.exe" : "legendary_windows_x64.exe";
            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() == wanted)
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(url)) return File.Exists(target);
            var tmp = target + ".download";
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }
            if (new FileInfo(tmp).Length < 1_000_000) { File.Delete(tmp); return File.Exists(target); }
            File.Move(tmp, target, true);
            return true;
        }
        catch { return File.Exists(target); }
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string arguments, int timeoutSeconds = 120, CancellationToken ct = default)
    {
        var exe = FindExe() ?? throw new FileNotFoundException("legendary.exe ble ikke funnet.");
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte Legendary.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw;
        }
    }

    /// <summary>Henter hele Epic-biblioteket (krever innlogging). Tom liste ved feil.</summary>
    public static async Task<List<EpicLibraryGame>> ListLibraryAsync(CancellationToken ct = default)
    {
        var list = new List<EpicLibraryGame>();
        if (!IsAvailable) return list;
        try
        {
            var (code, output, err) = await RunAsync("list --json", 180, ct);
            Remember("list --json", code, output.Length > 4000 ? output[..4000] : output, err);
            if (code != 0 || string.IsNullOrWhiteSpace(output)) return list;
            var start = output.IndexOf('[');
            if (start < 0) return list;
            using var doc = JsonDocument.Parse(output[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var g in doc.RootElement.EnumerateArray())
            {
                var app = g.TryGetProperty("app_name", out var a) ? a.GetString() : null;
                var title = g.TryGetProperty("app_title", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(title)) continue;
                if (app.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue;
                string? cover = null;
                try
                {
                    if (g.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object &&
                        (md.TryGetProperty("keyImages", out var imgs) || md.TryGetProperty("key_images", out imgs)) &&
                        imgs.ValueKind == JsonValueKind.Array)
                        cover = PickCover(imgs);
                }
                catch { }
                list.Add(new EpicLibraryGame(app, title, cover));
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        var sorted = list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (sorted.Count > 0) SaveLibraryCache(sorted);
        return sorted;
    }

    /// <summary>Importerer innlogging fra en eksisterende Epic Games Launcher. True ved suksess.</summary>
    public static async Task<bool> TryImportAuthAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        try
        {
            var (code, output, err) = await RunAsync("auth --import", 60, ct);
            Remember("auth --import", code, output, err);
            return code == 0;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }

    /// <summary>Logger inn med autorisasjonskode fra Epics innloggingsside.</summary>
    public static async Task<bool> TryAuthWithCodeAsync(string code, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(code)) return false;
        var clean = new string(code.Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length == 0) return false;
        try
        {
            var (exit, output, err) = await RunAsync($"auth --code {clean}", 60, ct);
            Remember("auth --code", exit, output, err);
            return exit == 0;
        }
        catch (Exception ex) { LastError = ex.Message; return false; }
    }
}
