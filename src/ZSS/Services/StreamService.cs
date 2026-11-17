using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ZenStationShell.Services;

public sealed record StreamHost(string Name, string Address);

/// <summary>
/// Streaming: klient = Moonlight (kommandolinjen, lastes ned første gang), vert = Apollo (Sunshine-fork med virtuell skjerm,
/// styres via nettgrensesnittets REST-API på https://localhost:47990).
/// Merk: ingen av delene er testet mot en ekte installasjon fra denne koden ennå. Alt som skjer logges til stream.log.
/// </summary>
public static class StreamService
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "stream");
    private static string MoonlightDir => Path.Combine(Dir, "moonlight");
    public static string LogFile => Path.Combine(Dir, "stream.log");

    private static readonly HttpClient Http = MakeHttp();
    private static HttpClient MakeHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenLauncher");
        return c;
    }
    // Apollo bruker et selvsignert sertifikat. Brukes KUN mot localhost.
    // Ingen automatisk omdirigering (uten innlogging sender Apollo oss til /welcome) og ingen cookie-håndtering: vi setter «auth»-cookien selv.
    private static readonly HttpClient Api = new(new HttpClientHandler
    { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(20) };
    private static string? _authCookie;
    private static string _authFor = "";

    /// <summary>Apollo bruker innloggingsøkt (cookie «auth»), ikke Basic-autentisering. POST /api/login gir cookien.</summary>
    private static bool _unreachable;

    private static async Task<bool> LoginAsync(string user, string pass)
    {
        _unreachable = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://localhost:47990/api/login")
            { Content = new StringContent(JsonSerializer.Serialize(new { username = user, password = pass }), Encoding.UTF8, "application/json") };
            using var r = await Api.SendAsync(req);
            var t = await r.Content.ReadAsStringAsync();
            Log($"POST /api/login -> {(int)r.StatusCode} {Short(t)}");
            if (r.StatusCode != HttpStatusCode.OK) return false;
            if (r.Headers.TryGetValues("Set-Cookie", out var vals))
                foreach (var v in vals)
                {
                    var m = Regex.Match(v, @"(?:^|[\s;])auth=([^;]+)");
                    if (m.Success) { _authCookie = m.Groups[1].Value; _authFor = user + ":" + pass; return true; }
                }
            Log("Login ok, men fant ingen auth-cookie.");
            return false;
        }
        catch (Exception ex) { Log("Login feilet: " + ex.Message); _unreachable = true; return false; }
    }

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 200_000) File.Delete(LogFile);
            File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }
    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ---------- Nedlasting ----------

    private static async Task<(string Url, string Name, string Tag)?> LatestAssetAsync(string repo, string namePattern, CancellationToken ct)
    {
        var json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (Regex.IsMatch(name, namePattern, RegexOptions.IgnoreCase))
                return (a.GetProperty("browser_download_url").GetString() ?? "", name, tag);
        }
        return null;
    }

    private static async Task DownloadAsync(string url, string path, Action<string> report, string label, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long done = 0; var last = DateTime.MinValue;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 400)
            {
                last = DateTime.UtcNow;
                report(total > 0 ? $"{label}: {done / 1048576.0:0.#} av {total / 1048576.0:0.#} MB" : $"{label}: {done / 1048576.0:0.#} MB");
            }
        }
    }

    // ---------- Klient: Moonlight ----------

    public static string? MoonlightExe
    {
        get
        {
            try
            {
                if (!Directory.Exists(MoonlightDir)) return null;
                return Directory.EnumerateFiles(MoonlightDir, "Moonlight.exe", SearchOption.AllDirectories).FirstOrDefault();
            }
            catch { return null; }
        }
    }

    public static async Task<(bool Ok, string Msg)> InstallMoonlightAsync(Action<string> report, CancellationToken ct = default)
    {
        try
        {
            report("Finner siste Moonlight…");
            var asset = await LatestAssetAsync("moonlight-stream/moonlight-qt", @"^MoonlightPortable-x64-.*\.zip$", ct);
            if (asset is null) return (false, "Fant ikke Moonlight Portable x64 i siste utgivelse.");
            Directory.CreateDirectory(Dir);
            var zip = Path.Combine(Dir, asset.Value.Name);
            await DownloadAsync(asset.Value.Url, zip, report, "Laster ned Moonlight", ct);
            report("Pakker ut…");
            if (Directory.Exists(MoonlightDir)) Directory.Delete(MoonlightDir, true);
            ZipFile.ExtractToDirectory(zip, MoonlightDir, true);
            try { File.Delete(zip); } catch { }
            Log("Moonlight installert: " + asset.Value.Tag);
            return MoonlightExe is null ? (false, "Pakket ut, men fant ikke Moonlight.exe.") : (true, "Moonlight " + asset.Value.Tag + " er installert.");
        }
        catch (Exception ex) { Log("InstallMoonlight: " + ex); return (false, "Feilet: " + ex.Message); }
    }

    private static async Task<(int Exit, string Output)> RunAsync(string exe, string args, int timeoutMs, CancellationToken ct = default, Func<string, bool>? done = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p is null) return (-1, "Kunne ikke starte " + exe);
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        // Moonlight avslutter ikke alltid etter «list»: når ønsket utskrift er kommet, venter vi kort og stenger den.
        bool finished = false;
        if (done is not null)
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!cts.IsCancellationRequested && !p.HasExited)
                    {
                        await Task.Delay(300, cts.Token);
                        string s; lock (sb) s = sb.ToString();
                        if (done(s)) { await Task.Delay(500, cts.Token); finished = true; cts.Cancel(); }
                    }
                }
                catch { }
            });
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            lock (sb) return (finished ? 0 : -2, sb.ToString() + (finished ? "" : "\n(tidsavbrudd)"));
        }
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    /// <summary>Paring: Moonlight venter til PIN-en er skrevet inn på verten.</summary>
    public static async Task<(bool Ok, string Msg)> PairAsync(string host, string pin, CancellationToken ct = default)
    {
        var exe = MoonlightExe;
        if (exe is null) return (false, "Moonlight er ikke installert.");
        var (code, output) = await RunAsync(exe, $"pair \"{host}\" --pin {pin}", 150_000, ct);
        Log($"pair {host} -> {code}: {Short(output)}");
        return (code == 0, code == 0 ? "Paret." : "Paring feilet: " + Short(output.Trim()));
    }

    public static async Task<(bool Ok, List<string> Apps, string Msg)> ListAppsAsync(string host)
    {
        var exe = MoonlightExe;
        if (exe is null) return (false, new List<string>(), "Moonlight er ikke installert.");
        var (code, output) = await RunAsync(exe, $"list \"{host}\" --csv", 20_000, default, o => o.Contains("Boxart URL", StringComparison.OrdinalIgnoreCase) && o.Split('\n').Any(l => l.TrimStart().StartsWith('"')));
        Log($"list {host} -> {code}: {Short(output)}");
        bool hasRows = output.Contains("Boxart URL", StringComparison.OrdinalIgnoreCase) || output.Split('\n').Any(l => l.TrimStart().StartsWith('"'));
        if (code != 0 && !hasRows) return (false, new List<string>(), Short(output.Trim()));
        var apps = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var name = line.StartsWith('"') ? line.Split('"')[1] : line.Split(',')[0].Trim();
            if (name.Length == 0 || name.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (!apps.Contains(name)) apps.Add(name);
        }
        return (true, apps, "");
    }

    public static Process? StartStream(string host, string app, int resIdx, int fpsIdx, int bitrateIdx, bool quitAfter)
    {
        var exe = MoonlightExe;
        if (exe is null) return null;
        string[] res = { "--720", "--1080", "--1440", "--4K" };
        int[] fps = { 30, 60, 120 };
        int[] mbit = { 10, 20, 40, 80 };
        var args = $"stream \"{host}\" \"{app}\" {res[Math.Clamp(resIdx, 0, 3)]} --fps {fps[Math.Clamp(fpsIdx, 0, 2)]} " +
                   $"--bitrate {mbit[Math.Clamp(bitrateIdx, 0, 3)] * 1000} --display-mode borderless " + (quitAfter ? "--quit-after" : "--no-quit-after");
        Log("stream " + args);
        try { return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception ex) { Log("StartStream: " + ex.Message); return null; }
    }

    /// <summary>Er verten oppe? Svarer på /serverinfo (port 47989). Returnerer vertsnavn.</summary>
    public static async Task<string?> ProbeHostAsync(string host)
    {
        try
        {
            using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var xml = await c.GetStringAsync($"http://{host}:47989/serverinfo");
            var doc = XDocument.Parse(xml);
            return doc.Root?.Element("hostname")?.Value ?? host;
        }
        catch { return null; }
    }

    // ---------- Oppdagelse i nettverket (mDNS, _nvstream._tcp) ----------

    public static async Task<List<StreamHost>> DiscoverAsync(int timeoutMs = 2500)
    {
        var found = new Dictionary<string, StreamHost>();
        var query = BuildQuery("_nvstream._tcp.local");
        var tasks = new List<Task>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var local = ua.Address;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var udp = new UdpClient(new IPEndPoint(local, 0));
                        await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
                        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                        while (true)
                        {
                            var left = end - DateTime.UtcNow;
                            if (left <= TimeSpan.Zero) break;
                            UdpReceiveResult r;
                            try { r = await udp.ReceiveAsync().WaitAsync(left); }
                            catch (TimeoutException) { break; }
                            foreach (var inst in ParsePtr(r.Buffer, "_nvstream._tcp.local"))
                            {
                                var name = inst.Split('.')[0];
                                var addr = r.RemoteEndPoint.Address.ToString();
                                lock (found) found[addr] = new StreamHost(name, addr);
                            }
                        }
                    }
                    catch (Exception ex) { Log("mDNS " + local + ": " + ex.Message); }
                }));
            }
        }
        await Task.WhenAll(tasks);
        Log("mDNS fant " + found.Count + " vert(er)");
        return found.Values.ToList();
    }

    private static byte[] BuildQuery(string name)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 12, 0x80, 1 }); // PTR, klasse IN med «unicast-svar»-biten
        return ms.ToArray();
    }

    private static string ReadName(byte[] b, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos; bool jumped = false; int guard = 0;
        while (p < b.Length && guard++ < 64)
        {
            int len = b[p];
            if (len == 0) { p++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int off = ((len & 0x3F) << 8) | b[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true; p = off; continue;
            }
            p++;
            sb.Append(Encoding.UTF8.GetString(b, p, len)).Append('.');
            p += len;
        }
        if (!jumped) pos = p;
        return sb.ToString().TrimEnd('.');
    }

    private static List<string> ParsePtr(byte[] b, string service)
    {
        var res = new List<string>();
        try
        {
            if (b.Length < 12) return res;
            int qd = (b[4] << 8) | b[5], an = (b[6] << 8) | b[7], ns = (b[8] << 8) | b[9], ar = (b[10] << 8) | b[11];
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(b, ref pos); pos += 4; }
            for (int i = 0; i < an + ns + ar; i++)
            {
                ReadName(b, ref pos);
                int type = (b[pos] << 8) | b[pos + 1];
                int rdlen = (b[pos + 8] << 8) | b[pos + 9];
                pos += 10;
                if (type == 12)
                {
                    int p2 = pos;
                    var target = ReadName(b, ref p2);
                    if (target.EndsWith(service, StringComparison.OrdinalIgnoreCase)) res.Add(target);
                }
                pos += rdlen;
            }
        }
        catch { }
        return res;
    }

    // ---------- Vert: Apollo ----------

    public static string? ApolloDir
    {
        get
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                foreach (var name in new[] { "Apollo", "Sunshine" })
                {
                    var d = Path.Combine(pf, name);
                    if (File.Exists(Path.Combine(d, "sunshine.exe"))) return d;
                }
            return null;
        }
    }

    public static string NewPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    /// <summary>Laster ned og installerer Apollo stille, setter innlogging og starter tjenesten på nytt. Ett UAC-spørsmål.</summary>
    public static async Task<(bool Ok, string Msg)> InstallApolloAsync(string user, string pass, Action<string> report, CancellationToken ct = default)
    {
        try
        {
            report("Finner siste Apollo…");
            var asset = await LatestAssetAsync("ClassicOldSong/Apollo", @"^Apollo-.*\.exe$", ct);
            if (asset is null) return (false, "Fant ikke Apollo-installasjonsfilen i siste utgivelse.");
            Directory.CreateDirectory(Dir);
            var setup = Path.Combine(Dir, asset.Value.Name);
            await DownloadAsync(asset.Value.Url, setup, report, "Laster ned Apollo", ct);

            var log = Path.Combine(Dir, "apollo-setup.log");
            var script = Path.Combine(Dir, "apollo-setup.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"echo === {DateTime.Now:s} > \"{log}\"\r\n" +
                $"start /wait \"\" \"{setup}\" /S >> \"{log}\" 2>&1\r\n" +
                "set D=\r\n" +
                "if exist \"%ProgramFiles%\\Apollo\\sunshine.exe\" set D=%ProgramFiles%\\Apollo\r\n" +
                "if \"%D%\"==\"\" if exist \"%ProgramFiles%\\Sunshine\\sunshine.exe\" set D=%ProgramFiles%\\Sunshine\r\n" +
                $"if \"%D%\"==\"\" (echo ikke installert >> \"{log}\" & exit /b 2)\r\n" +
                "for %%S in (ApolloService SunshineService) do (sc query %%S >nul 2>&1 && (net stop %%S >nul 2>&1 & net start %%S >nul 2>&1))\r\n" +
                "exit /b 0\r\n");

            report("Installerer Apollo (Windows spør om tillatelse)…");
            Process? p;
            try
            {
                p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, "Avbrutt: du trykket Nei i Windows-spørsmålet."); }
            if (p is null) return (false, "Kunne ikke starte installasjonen.");
            await p.WaitForExitAsync(ct);
            Log($"apollo-setup exit {p.ExitCode}");
            try { File.Delete(setup); } catch { }
            if (p.ExitCode == 2 || ApolloDir is null) return (false, "Installasjonen ble ikke funnet etterpå. Se " + log);
            return (true, "Apollo " + asset.Value.Tag + " er installert.");
        }
        catch (Exception ex) { Log("InstallApollo: " + ex); return (false, "Feilet: " + ex.Message); }
    }

    /// <summary>Setter Apollo-tjenesten til automatisk start og starter den nå. Ett UAC-spørsmål. Exit 3 = fant ingen tjeneste.</summary>
    public static async Task<(bool Ok, string Msg)> EnsureApolloServiceAsync()
    {
        try
        {
            var script = Path.Combine(Dir, "apollo-service.cmd");
            var log = Path.Combine(Dir, "apollo-service.log");
            Directory.CreateDirectory(Dir);
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"echo === {DateTime.Now:s} > \"{log}\"\r\n" +
                "set F=0\r\n" +
                $"for %%S in (ApolloService SunshineService) do (sc query %%S >> \"{log}\" 2>&1 && (set F=1 & sc config %%S start= auto >> \"{log}\" 2>&1 & net start %%S >> \"{log}\" 2>&1))\r\n" +
                "if \"%F%\"==\"0\" exit /b 3\r\n" +
                "exit /b 0\r\n");
            Process? p;
            try { p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (false, "Avbrutt: du trykket Nei i Windows-spørsmålet."); }
            if (p is null) return (false, "Kunne ikke starte.");
            await p.WaitForExitAsync();
            Log($"apollo-service exit {p.ExitCode}");
            if (p.ExitCode == 3) return (false, "Fant ingen Apollo-tjeneste i Windows. Se " + log);
            return (true, "Apollo starter nå automatisk med Windows.");
        }
        catch (Exception ex) { Log("EnsureApolloService: " + ex.Message); return (false, "Feilet: " + ex.Message); }
    }

    /// <summary>Finnes virtuell skjerm-driveren (SudoVDA)? Sjekker pnputil. Navnet på enheten er ikke verifisert, så vi leter etter «sudo».</summary>
    public static async Task<bool> VirtualDriverPresentAsync()
    {
        try
        {
            var (_, o) = await RunAsync("pnputil.exe", "/enum-devices", 20_000);
            return o.Contains("sudovda", StringComparison.OrdinalIgnoreCase) || o.Contains("sudomaker", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static async Task<(int Status, string Body)> ApiAsync(HttpMethod m, string path, string user, string pass, object? body = null, bool quiet = false)
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_authCookie is null || _authFor != user + ":" + pass)
                    if (!await LoginAsync(user, pass)) return (_unreachable ? 0 : 401, _unreachable ? "Apollo svarer ikke" : "Innlogging feilet");
                using var req = new HttpRequestMessage(m, "https://localhost:47990" + path);
                req.Headers.TryAddWithoutValidation("Cookie", "auth=" + _authCookie);
                if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var r = await Api.SendAsync(req);
                var t = await r.Content.ReadAsStringAsync();
                if (!quiet) Log($"{m} {path} -> {(int)r.StatusCode} {Short(t)}");
                int code = (int)r.StatusCode;
                // Uten gyldig økt omdirigerer Apollo til innloggings-/oppsettsiden (HTML).
                bool unauth = code is 401 or 301 or 302 or 303 or 307 or 308 || (code == 200 && t.TrimStart().StartsWith("<", StringComparison.Ordinal));
                if (unauth) { _authCookie = null; if (attempt == 0) continue; return (401, t); }
                return (code, t);
            }
            return (401, "");
        }
        catch (Exception ex) { Log($"{m} {path} feilet: {ex.Message}"); return (0, ex.Message); }
    }

    /// <summary>Første oppsett: lager innlogging via /api/password. Apollo tillater dette uten innlogging bare når ingen finnes fra før.</summary>
    public static async Task<(bool Ok, string Msg)> SetupCredsAsync(string user, string pass)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://localhost:47990/api/password")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    currentUsername = "", currentPassword = "", newUsername = user, newPassword = pass, confirmNewPassword = pass,
                }), Encoding.UTF8, "application/json"),
            };
            using var r = await Api.SendAsync(req);
            var t = await r.Content.ReadAsStringAsync();
            Log($"POST /api/password -> {(int)r.StatusCode} {Short(t)}");
            if ((int)r.StatusCode == 200 && !t.TrimStart().StartsWith("<", StringComparison.Ordinal) && !t.Contains("\"status\":false", StringComparison.OrdinalIgnoreCase)) { _authCookie = null; return (true, "Innlogging laget."); }
            if ((int)r.StatusCode is 301 or 302 or 303 or 307 or 308) return (false, "Apollo har allerede en innlogging som ZenLauncher ikke kjenner.");
            if ((int)r.StatusCode is 401 or 403) return (false, "Apollo har allerede en innlogging som ZenLauncher ikke kjenner. Åpne Apollo, bytt brukernavn/passord der, eller avinstaller og prøv igjen.");
            return (false, "Kunne ikke lage innlogging (" + (int)r.StatusCode + ").");
        }
        catch (Exception ex) { Log("SetupCreds: " + ex.Message); return (false, "Apollo svarer ikke: " + ex.Message); }
    }

    public static async Task<(bool Ok, string Msg)> SubmitPinAsync(string pin, string name, string user, string pass)
    {
        var (s, b) = await ApiAsync(HttpMethod.Post, "/api/pin", user, pass, new { pin, name });
        if (s != 200) return (false, s == 401 ? "Feil innlogging mot Apollo. Trykk Sett opp." : $"Apollo svarte med feil ({s}): {Short(b)}");
        bool ok = b.Contains("\"status\":true", StringComparison.OrdinalIgnoreCase) || b.Contains("\"status\": true", StringComparison.OrdinalIgnoreCase);
        return (ok, ok ? "Paret med «" + name + "»." : "Apollo godtok ikke PIN-en. Start paringen på klienten først (Par), og skriv PIN-en her innen et par minutter.");
    }

    /// <summary>Er noen koblet til og strømmer akkurat nå? Stille (logger ikke), brukes i en tidtaker.</summary>
    public static async Task<bool?> AnyClientConnectedAsync(string user, string pass)
    {
        var (s, b) = await ApiAsync(HttpMethod.Get, "/api/clients/list", user, pass, null, true);
        if (s != 200) return null;
        try
        {
            using var doc = JsonDocument.Parse(b);
            if (doc.RootElement.TryGetProperty("named_certs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    if (c.TryGetProperty("connected", out var v) && v.ValueKind == JsonValueKind.True) return true;
            return false;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Samme som Windows-snarveien Ctrl+Win+Shift+B: starter grafikkdriveren på nytt, så skjermoppsett og oppløsning hentes på nytt.</summary>
    public static void ResetGraphicsStack()
    {
        byte[] keys = { 0x5B, 0x11, 0x10, 0x42 }; // Win, Ctrl, Shift, B
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in keys.Reverse()) keybd_event(k, 0, 2, UIntPtr.Zero);
        Log("Grafikkdriver tilbakestilt (Ctrl+Win+Shift+B).");
    }

    public static async Task<List<string>> PairedClientsAsync(string user, string pass)
    {
        var res = new List<string>();
        var (s, b) = await ApiAsync(HttpMethod.Get, "/api/clients/list", user, pass);
        if (s != 200) return res;
        try
        {
            using var doc = JsonDocument.Parse(b);
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Walk(x);
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) res.Add(n.GetString() ?? "");
                    else foreach (var p in e.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) Walk(p.Value);
                }
            }
            Walk(doc.RootElement);
        }
        catch { }
        return res.Where(x => x.Length > 0).ToList();
    }

    /// <summary>Slår sammen med eksisterende innstillinger (POST /api/config erstatter hele filen) og starter Apollo på nytt.</summary>
    public static async Task<(bool Ok, string Msg)> SetConfigAsync(Dictionary<string, string> changes, string user, string pass)
    {
        var (s, b) = await ApiAsync(HttpMethod.Get, "/api/config", user, pass);
        if (s != 200) return (false, s == 401 ? "Feil innlogging mot Apollo." : "Apollo svarer ikke (kjører den?).");
        Dictionary<string, JsonElement> cur;
        try { cur = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b) ?? new(); }
        catch (Exception ex) { Log("SetConfig parse: " + ex.Message); return (false, "Uventet svar fra Apollo."); }
        foreach (var k in new[] { "status", "platform", "version" }) cur.Remove(k);
        var merged = new Dictionary<string, object?>();
        foreach (var kv in cur) merged[kv.Key] = kv.Value;
        foreach (var kv in changes) merged[kv.Key] = kv.Value;
        var (s2, _) = await ApiAsync(HttpMethod.Post, "/api/config", user, pass, merged);
        if (s2 != 200) return (false, "Kunne ikke lagre innstillingene (" + s2 + ").");
        await ApiAsync(HttpMethod.Post, "/api/restart", user, pass, new { });
        return (true, "Lagret. Apollo starter på nytt.");
    }

    /// <summary>Slår «Always use Virtual Display» på/av for ALLE appene i Apollo (Desktop, Steam Big Picture osv.). Leser tilbake for å sjekke.</summary>
    public static async Task<(bool Ok, string Msg)> SetDesktopVirtualAsync(bool on, string user, string pass)
    {
        var (s, b) = await ApiAsync(HttpMethod.Get, "/api/apps", user, pass);
        if (s != 200) return (false, "Apollo svarer ikke (kjører den?).");
        var list = new List<Dictionary<string, JsonElement>>();
        using (var doc = JsonDocument.Parse(b))
        {
            if (!doc.RootElement.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return (false, "Uventet svar fra Apollo.");
            foreach (var a in apps.EnumerateArray())
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.GetRawText());
                if (d is not null) list.Add(d);
            }
        }
        if (list.Count == 0) return (false, "Apollo har ingen apper.");

        for (int idx = 0; idx < list.Count; idx++)
        {
            var body = new Dictionary<string, object?>();
            foreach (var kv in list[idx]) body[kv.Key] = kv.Value;
            body["virtual-display"] = on;
            body["index"] = idx;
            var (s2, _) = await ApiAsync(HttpMethod.Post, "/api/apps", user, pass, body);
            if (s2 != 200) return (false, $"Kunne ikke endre appen nummer {idx + 1} i Apollo ({s2}).");
        }

        var (s3, b3) = await ApiAsync(HttpMethod.Get, "/api/apps", user, pass);
        if (s3 == 200)
        {
            using var doc = JsonDocument.Parse(b3);
            int total = 0, ok = 0;
            foreach (var a in doc.RootElement.GetProperty("apps").EnumerateArray())
            {
                total++;
                if (a.TryGetProperty("virtual-display", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean() == on) ok++;
            }
            if (ok == total && total > 0)
                return (true, on ? $"Alle appene ({total}) bruker nå virtuell skjerm." : "Appene bruker ikke virtuell skjerm.");
            return (false, $"Apollo tok imot endringen for {ok} av {total} apper. Åpne Apollo og huk av «Always use Virtual Display» manuelt for resten.");
        }
        return (false, "Kunne ikke sjekke endringen i Apollo.");
    }
}
