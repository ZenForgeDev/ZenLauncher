using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace ZenStationShell;

public partial class App : Application
{
    private static MainWindow? _main;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Vinduet går i fullskjerm i konstruktøren, FØR Activate (som en ekte hjem-app).
        _main = new MainWindow();
        _main.Activate();
        // «Start skjult i systemkurven»: bare ved oppstart med --tray (fra Windows-oppstart), ikke når du starter appen selv.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)) && _main.StartHiddenWanted)
            _main.HideToTray();
    }

    /// <summary>Kalles når en ny start av appen (f.eks. Game Bar «Home») er sendt hit. Kan komme fra en annen tråd.</summary>
    internal static void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        var win = _main;
        win?.DispatcherQueue.TryEnqueue(win.BringToFront);
    }
}
