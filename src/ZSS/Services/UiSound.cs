using Windows.Media.Core;
using Windows.Media.Playback;

namespace ZenStationShell.Services;

public enum Sfx { Move, Select, Back, Open, Launch }

/// <summary>Spiller korte UI-lyder (egne WAV-filer) med justerbart volum fra innstillingene.</summary>
public sealed class UiSound
{
    private readonly SettingsStore _settings;
    private readonly Dictionary<Sfx, MediaPlayer> _players = new();

    public UiSound(SettingsStore settings)
    {
        _settings = settings;
        foreach (var kind in Enum.GetValues<Sfx>())
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", kind.ToString().ToLowerInvariant() + ".wav");
                if (!File.Exists(path)) continue;
                var p = new MediaPlayer { AutoPlay = false, Volume = Volume };
                p.Source = MediaSource.CreateFromUri(new Uri(path));
                _players[kind] = p;
            }
            catch { /* manglende lyd skal aldri felle appen */ }
        }
    }

    private double Volume => Math.Clamp(_settings.Data.SoundVolume, 0, 100) / 100.0;

    public void Play(Sfx kind)
    {
        if (!_settings.Data.SoundEnabled || !_players.TryGetValue(kind, out var p)) return;
        try
        {
            p.Volume = Volume;
            p.PlaybackSession.Position = TimeSpan.Zero;
            p.Play();
        }
        catch { }
    }
}
