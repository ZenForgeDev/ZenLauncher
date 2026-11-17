using System.Diagnostics;
using System.Text.Json;

namespace ZenStationShell.Services;

/// <summary>
/// Finner installerte apper: Startmeny-snarveier (alle brukere + gjeldende bruker) og Store/UWP-apper via Get-StartApps.
/// Store-apper startes med shell:AppsFolder\AUMID, som Process.Start (UseShellExecute) forstår.
/// </summary>
public static class InstalledApps
{
    private static readonly string[] Skip =
    {
        "uninstall", "avinstaller", "readme", "read me", "help", "hjelp", "documentation", "dokumentasjon",
        "release notes", "license", "lisens", "website", "web site", "nettside", "manual", "changelog",
        "configure", "konfigurer", "repair", "reparer", "setup", "installer",
    };

    public static async Task<List<AppEntry>> ScanAsync()
    {
        var all = new List<AppEntry>();
        try { all.AddRange(await Task.Run(ScanShortcuts)); } catch { }
        try { all.AddRange(await ScanStoreAppsAsync()); } catch { }

        // Fjern dubletter på navn og mål.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppEntry>();
        foreach (var a in all.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (names.Contains(a.Name) || targets.Contains(a.Target)) continue;
            names.Add(a.Name);
            targets.Add(a.Target);
            result.Add(a);
        }
        return result;
    }

    private static bool Skipped(string name)
    {
        foreach (var s in Skip)
            if (name.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static List<AppEntry> ScanShortcuts()
    {
        var list = new List<AppEntry>();
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
        };
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return list;
        dynamic? shell = Activator.CreateInstance(type);
        if (shell is null) return list;

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var lnk in files)
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (Skipped(name)) continue;
                    dynamic sc = shell.CreateShortcut(lnk);
                    string target = sc.TargetPath ?? "";
                    string args = sc.Arguments ?? "";
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    // Kun vanlige programmer; snarveier til Windows\Installer og ikke-exe hoppes over.
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (target.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(target)) continue;
                    if (Path.GetFileName(target).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new AppEntry(name, target, string.IsNullOrWhiteSpace(args) ? null : args));
                }
                catch { }
            }
        }
        return list;
    }

    private static async Task<List<AppEntry>> ScanStoreAppsAsync()
    {
        var list = new List<AppEntry>();
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p is null) return list;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };
        foreach (var e in items)
        {
            var name = e.TryGetProperty("Name", out var n) ? n.GetString() : null;
            var id = e.TryGetProperty("AppID", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
            // Bare Store/UWP-apper (AUMID med '!'); vanlige programmer dekkes av snarveiene.
            if (!id.Contains('!')) continue;
            if (Skipped(name)) continue;
            list.Add(new AppEntry(name, @"shell:AppsFolder\" + id));
        }
        return list;
    }
}
