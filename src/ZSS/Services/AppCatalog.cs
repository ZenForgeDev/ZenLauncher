namespace ZenStationShell.Services;

/// <summary>Finner kjente programmer og nettsider som kan legges til på hjemskjermen.</summary>
public static class AppCatalog
{
    public static List<AppEntry> Candidates(IEnumerable<AppEntry> already)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var list = new List<AppEntry>();

        void Exe(string name, params string[] paths)
        {
            var p = paths.FirstOrDefault(File.Exists);
            if (p is not null) list.Add(new AppEntry(name, p));
        }

        Exe("Microsoft Edge", Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
        Exe("Google Chrome", Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(local, @"Google\Chrome\Application\chrome.exe"));
        Exe("Firefox", Path.Combine(pf, @"Mozilla Firefox\firefox.exe"), Path.Combine(pf86, @"Mozilla Firefox\firefox.exe"));
        Exe("Brave", Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe"));
        Exe("Opera GX", Path.Combine(local, @"Programs\Opera GX\opera.exe"));
        Exe("Spotify", Path.Combine(roam, @"Spotify\Spotify.exe"));

        list.Add(new AppEntry("Filutforsker", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
        list.Add(new AppEntry("YouTube", "https://www.youtube.com"));
        list.Add(new AppEntry("Netflix", "https://www.netflix.com"));
        list.Add(new AppEntry("Twitch", "https://www.twitch.tv"));
        list.Add(new AppEntry("Disney+", "https://www.disneyplus.com"));
        list.Add(new AppEntry("Viaplay", "https://viaplay.no"));

        var have = new HashSet<string>(already.Select(a => a.Target), StringComparer.OrdinalIgnoreCase);
        return list.Where(a => !have.Contains(a.Target)).ToList();
    }
}
