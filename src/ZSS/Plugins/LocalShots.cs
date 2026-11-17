namespace ZenStationShell.Plugins;

/// <summary>Plugin: dine egne Steam-skjermbilder for et spill, lest fra Steams mapper på PC-en. Ingen nettverk.</summary>
public static class LocalShots
{
    public const string Id = "shots";

    public static List<string> Find(int appId, int max = 8)
    {
        var all = new List<FileInfo>();
        try
        {
            var steam = ZenStationShell.Services.SteamLibrary.GetSteamPath();
            if (steam is null) return new();
            var users = Path.Combine(steam, "userdata");
            if (!Directory.Exists(users)) return new();
            foreach (var u in Directory.EnumerateDirectories(users))
            {
                var dir = Path.Combine(u, "760", "remote", appId.ToString(), "screenshots");
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.jpg")) all.Add(new FileInfo(f));
            }
        }
        catch { }
        return all.OrderByDescending(f => f.LastWriteTimeUtc).Take(max).Select(f => f.FullName).ToList();
    }
}
