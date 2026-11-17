using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace ZenStationShell.Services;

/// <summary>
/// Start med Windows. Vanlig oppstart bruker Run-nøkkelen i registeret (Windows venter litt med slike apper).
/// Rask oppstart bruker Oppgaveplanlegger (Task Scheduler) med innloggingsutløser uten forsinkelse og høyere prioritet.
/// Appen startes via programaliaset «zenlauncher.exe» (med --tray for å starte skjult i systemkurven).
/// </summary>
public static class StartupService
{
    public const string TaskName = "ZenLauncher oppstart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ZenLauncher";

    private static string AliasPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "zenlauncher.exe");

    /// <summary>Kommandoen som starter appen. Aliaset er det beste (kan ta imot --tray); ellers startes pakken via Utforsker.</summary>
    private static (string Exe, string Args) LaunchCommand(bool tray)
    {
        if (File.Exists(AliasPath)) return (AliasPath, tray ? "--tray" : "");
        try
        {
            var pfn = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            return ("explorer.exe", $"shell:AppsFolder\\{pfn}!App");
        }
        catch { return (Environment.ProcessPath ?? "ZenTuneLauncher.exe", tray ? "--tray" : ""); }
    }

    public static async Task<(bool Ok, string Msg)> ApplyAsync(bool start, bool fast, bool tray)
    {
        try
        {
            var (exe, args) = LaunchCommand(tray);
            string note = !File.Exists(AliasPath) ? " (programaliaset finnes ikke ennå, så «start skjult» virker først etter ny bygging og registrering)" : "";

            // Fjern alt først, sett så opp det som er valgt.
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) k?.DeleteValue(RunValue, false);
            bool taskWanted = start && fast;
            if (!taskWanted) await DeleteTaskAsync();

            if (!start) return (true, "Starter ikke med Windows.");

            if (!fast)
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                k.SetValue(RunValue, $"\"{exe}\" {args}".TrimEnd());
                return (true, "Starter med Windows." + note);
            }

            var (ok, msg) = await CreateTaskAsync(exe, args);
            return ok ? (true, "Rask oppstart er satt opp i Oppgaveplanlegger." + note) : (false, msg);
        }
        catch (Exception ex) { return (false, "Feilet: " + ex.Message); }
    }

    private static string TaskXml(string exe, string args)
    {
        var user = SecurityElement.Escape(Environment.UserDomainName + "\\" + Environment.UserName);
        return
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
            "  <RegistrationInfo><Description>Starter ZenLauncher raskt når du logger inn.</Description></RegistrationInfo>\r\n" +
            $"  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT0S</Delay></LogonTrigger></Triggers>\r\n" +
            $"  <Principals><Principal id=\"Author\"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
            "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
            "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority><Enabled>true</Enabled></Settings>\r\n" +
            $"  <Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(exe)}</Command>" +
            (string.IsNullOrEmpty(args) ? "" : $"<Arguments>{SecurityElement.Escape(args)}</Arguments>") +
            "</Exec></Actions>\r\n</Task>\r\n";
    }

    private static async Task<(bool Ok, string Msg)> CreateTaskAsync(string exe, string args)
    {
        Directory.CreateDirectory(StreamService.Dir);
        var xml = Path.Combine(StreamService.Dir, "startup-task.xml");
        File.WriteAllText(xml, TaskXml(exe, args), Encoding.Unicode);
        var cmd = $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F";
        var (code, output) = await RunAsync("schtasks.exe", cmd, false);
        if (code != 0)
        {
            StreamService.Log($"schtasks create (vanlig) -> {code}: {output.Trim()}");
            // Noen PC-er krever administrator for å lage oppgaver. Prøv igjen med ett UAC-spørsmål.
            (code, output) = await RunAsync("schtasks.exe", cmd, true);
            StreamService.Log($"schtasks create (admin) -> {code}: {output.Trim()}");
        }
        return code == 0 ? (true, "") : (false, code == -1223 ? "Avbrutt: du trykket Nei i Windows-spørsmålet." : "Kunne ikke lage oppgaven i Oppgaveplanlegger. Se stream.log.");
    }

    private static async Task DeleteTaskAsync()
    {
        var (code, _) = await RunAsync("schtasks.exe", $"/Query /TN \"{TaskName}\"", false);
        if (code != 0) return; // finnes ikke
        var (c2, o2) = await RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", false);
        if (c2 != 0) await RunAsync("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", true);
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args, bool elevated)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; }
            else { psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; }
            using var p = Process.Start(psi);
            if (p is null) return (-1, "kunne ikke starte");
            string o = "";
            if (!elevated) o = (await p.StandardOutput.ReadToEndAsync()) + (await p.StandardError.ReadToEndAsync());
            await p.WaitForExitAsync();
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return (-1223, "avbrutt"); }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
