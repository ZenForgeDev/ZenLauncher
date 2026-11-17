using Windows.Media.Core;
using Windows.Media.Playback;

namespace ZenStationShell.Services;

/// <summary>
/// Bakgrunnsmusikk fra brukerens egen mappe (%LOCALAPPDATA%\ZenStationShell\Music). Av som standard.
/// Spilles bare mens ZenStation er synlig: pauses når et spill eller en annen app overtar, og fortsetter når du er tilbake.
/// </summary>
public sealed class BackgroundMusic : IDisposable
{
    private static readonly string[] Extensions = { ".mp3", ".wav", ".flac", ".m4a", ".wma", ".aac" };

    private readonly SettingsStore _settings;
    private MediaPlayer? _player;
    private bool _active = true;
    private int _loadedCount = -1;

    public BackgroundMusic(SettingsStore settings) => _settings = settings;

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "Music");

    /// <summary>Antall lydfiler i musikkmappen.</summary>
    public static int CountFiles()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            return Directory.EnumerateFiles(Folder).Count(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch { return 0; }
    }

    public void SetActive(bool active)
    {
        _active = active;
        Apply();
    }

    /// <summary>Starter, pauser eller stopper ut fra innstillingene og om vinduet er synlig.</summary>
    public void Apply()
    {
        try
        {
            bool want = _settings.Data.MusicEnabled && _active;
            if (!want)
            {
                _player?.Pause();
                if (!_settings.Data.MusicEnabled) Release();
                return;
            }

            if (_player is null) Load();
            if (_player is null) return;
            _player.Volume = Math.Clamp(_settings.Data.MusicVolume, 0, 100) / 100.0;
            if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing) _player.Play();
        }
        catch { /* musikk skal aldri felle appen */ }
    }

    /// <summary>Leser mappen på nytt (etter at du har lagt til filer).</summary>
    public void Reload()
    {
        Release();
        Apply();
    }

    private void Load()
    {
        var files = new List<string>();
        try
        {
            Directory.CreateDirectory(Folder);
            files = Directory.EnumerateFiles(Folder)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch { }
        _loadedCount = files.Count;
        if (files.Count == 0) return;

        var list = new MediaPlaybackList { AutoRepeatEnabled = true, ShuffleEnabled = true };
        foreach (var f in files)
            list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(f))));

        _player = new MediaPlayer { AutoPlay = false, Source = list };
    }

    private void Release()
    {
        try { _player?.Pause(); _player?.Dispose(); } catch { }
        _player = null;
        _loadedCount = -1;
    }

    public void Dispose() => Release();
}
