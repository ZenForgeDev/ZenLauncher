using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZenStationShell.Services;

/// <summary>Diagnose og sikkerhetskopi av innstillinger. Passord og nøkler tas ikke med i kopiene.</summary>
public static class ToolsService
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell");
    private static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    private static readonly string[] Secrets = { "ApolloPass", "SteamApiKey" };

    public static string DiagReport(string appVersion, SettingsData d)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ZenLauncher " + appVersion);
        sb.AppendLine("Windows: " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")");
        sb.AppendLine(".NET: " + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("Modus: " + (d.DesktopMode ? "desktop" : "konsoll") + ", streaming: " + (d.StreamMode switch { 1 => "klient", 2 => "vert", _ => "av" }));
        sb.AppendLine("Moonlight: " + (StreamService.MoonlightExe is null ? "ikke installert" : "installert"));
        sb.AppendLine("Apollo: " + (StreamService.ApolloDir is null ? "ikke installert" : "installert") + ", innlogging " + (d.ApolloUser.Length > 0 ? "satt" : "ikke satt"));
        sb.AppendLine("Verter: " + d.StreamHosts.Count + ", plugins på: " + d.Plugins.Count(x => x.Value));
        sb.AppendLine("Oppdateringssjekk: " + (d.CheckUpdates ? "på" : "av") + ", repo: " + (d.UpdateRepo.Length > 0 ? d.UpdateRepo : "ikke satt"));
        sb.AppendLine("Innstillingsfil: " + SettingsPath + (File.Exists(SettingsPath) ? " (" + new FileInfo(SettingsPath).Length + " B)" : " (mangler)"));
        try
        {
            if (File.Exists(StreamService.LogFile))
            {
                sb.AppendLine();
                sb.AppendLine("--- siste linjer i stream.log ---");
                foreach (var l in File.ReadLines(StreamService.LogFile).TakeLast(40)) sb.AppendLine(l);
            }
        }
        catch { }
        return sb.ToString();
    }

    private static void RemoveKeyCI(JsonObject o, string key)
    {
        foreach (var k in o.Select(x => x.Key).ToList())
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) o.Remove(k);
    }

    /// <summary>Lager en kopi av innstillingene i backups-mappen (uten passord og nøkler). Beholder de 10 siste.</summary>
    public static (bool Ok, string Msg) Backup()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return (false, "Fant ingen innstillingsfil å kopiere ennå.");
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            if (node is null) return (false, "Innstillingsfilen kunne ikke leses.");
            foreach (var s in Secrets) RemoveKeyCI(node, s);
            Directory.CreateDirectory(BackupDir);
            var path = Path.Combine(BackupDir, $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var old = Directory.GetFiles(BackupDir, "settings-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(10).ToList();
            foreach (var f in old) { try { File.Delete(f); } catch { } }
            return (true, "Kopi lagret: " + path);
        }
        catch (Exception ex) { return (false, "Kunne ikke ta kopi: " + ex.Message); }
    }

    public static int BackupCount()
    {
        try { return Directory.Exists(BackupDir) ? Directory.GetFiles(BackupDir, "settings-*.json").Length : 0; }
        catch { return 0; }
    }

    /// <summary>Legger den nyeste kopien tilbake. Innlogging og nøkler beholdes fra nåværende innstillinger. Dagens fil lagres først som before-restore-….</summary>
    public static (bool Ok, string Msg) RestoreLatest(SettingsData current)
    {
        try
        {
            if (!Directory.Exists(BackupDir)) return (false, "Ingen kopier funnet.");
            var latest = Directory.GetFiles(BackupDir, "settings-*.json").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (latest is null) return (false, "Ingen kopier funnet.");
            var node = JsonNode.Parse(File.ReadAllText(latest)) as JsonObject;
            if (node is null) return (false, "Kopien kunne ikke leses.");
            if (File.Exists(SettingsPath))
                File.Copy(SettingsPath, Path.Combine(BackupDir, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.json"), true);
            foreach (var s in Secrets) RemoveKeyCI(node, s);
            node["ApolloUser"] = current.ApolloUser;
            node["ApolloPass"] = current.ApolloPass;
            node["SteamApiKey"] = current.SteamApiKey;
            File.WriteAllText(SettingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "Gjenopprettet fra " + Path.GetFileName(latest));
        }
        catch (Exception ex) { return (false, "Kunne ikke gjenopprette: " + ex.Message); }
    }
}
