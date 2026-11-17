using ZenTune.Core.Games;
using ZenTune.Core.Hardware;
using ZenTune.Core.Presets;

namespace ZenStationShell.Services;

/// <summary>
/// Bro mellom ZenStation og ZenTune.Core: finner ZenTune-spillet som hører til en flis,
/// lager forhåndsvisning av innstillinger og skriver dem (med backup først).
/// All tung jobb kjøres utenfor UI-tråden.
/// </summary>
public static class TuneBridge
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<DetectedGame>? _games;
    private static HardwareProfile? _hw;

    /// <summary>ZenTune-spillet for en Steam-flis, eller null hvis ZenTune ikke kjenner spillet.</summary>
    public static async Task<DetectedGame?> FindAsync(GameEntry g)
    {
        await Gate.WaitAsync();
        try
        {
            _games ??= await Task.Run(() => GameScanner.Scan().Supported.ToList());
            var id = g.AppId.ToString();
            return _games.FirstOrDefault(d => d.App.Store == GameStore.Steam && d.App.Id == id);
        }
        finally { Gate.Release(); }
    }

    public static async Task<HardwareProfile> HardwareAsync()
    {
        _hw ??= await Task.Run(() => HardwareProfile.From(HardwareDetector.Detect()));
        return _hw;
    }

    /// <summary>Starter en ny skanning neste gang (f.eks. etter nye spill).</summary>
    public static void Invalidate() => _games = null;

    public static IGameTuner? TunerFor(DetectedGame d) => PresetService.For(d);

    public static Task<PresetPlan> PlanAsync(DetectedGame d, IGameTuner tuner, PresetKind kind, HardwareProfile hw)
        => Task.Run(() => tuner.Plan(d, kind, hw));

    /// <summary>Skriver planen. Spillet må være lukket. ZenTune tar backup først.</summary>
    public static Task<int> ApplyAsync(PresetPlan plan) => Task.Run(() => PresetService.Apply(plan));

    public static bool IsRunning(DetectedGame d) => GameScanner.IsRunning(d);

    public static List<BackupInfo> BackupsFor(DetectedGame d) => Backups.List(d.Id);

    public static Task RestoreAsync(DetectedGame d, BackupInfo b) => Task.Run(() => Backups.Restore(d, b));

    /// <summary>Leser filene på nytt etter endring, så «antall endringer» stemmer.</summary>
    public static async Task<DetectedGame> RefreshAsync(DetectedGame d)
    {
        var fresh = await Task.Run(() => GameScanner.Refresh(d));
        await Gate.WaitAsync();
        try
        {
            if (_games is not null)
            {
                int i = _games.FindIndex(x => x.Id == d.Id);
                if (i >= 0) _games[i] = fresh;
            }
        }
        finally { Gate.Release(); }
        return fresh;
    }
}
