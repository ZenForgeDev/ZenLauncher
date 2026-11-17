using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace ZenStationShell;

/// <summary>
/// Egen Main for å sikre at bare én ZenStation kjører. Starter Windows (Game Bar «Home», Oppgavevisning,
/// oppstart) appen en gang til, sendes aktiveringen videre til den som kjører, og den nye avslutter.
/// </summary>
public static class Program
{
    private const string InstanceKey = "ZenForge.ZenTuneLauncher.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance(args))
            return 0;

        Microsoft.UI.Xaml.Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool RedirectToExistingInstance(string[] args)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (main.IsCurrent)
        {
            main.Activated += App.OnRedirectedActivation;
            return false;
        }

        // Oppstart med --tray når appen allerede kjører: ikke dra den fram.
        if (args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))) return true;
        main.RedirectActivationToAsync(activationArgs).AsTask().Wait();
        return true;
    }
}
