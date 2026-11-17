using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

/// <summary>
/// Kontroller som mus i andre apper (nettleser, Windows-innstillinger). Bruker XInput, som virker
/// også når ZenStation ikke har fokus. Er av mens ZenStation selv er i forgrunnen.
/// Venstrestikk = peker, høyrestikk = rulling, A = venstreklikk, B = høyreklikk,
/// stikk-trykk (R3) = midtklikk, LB/RB = tilbake/fram, LT = presisjon, RT = rask, Y = tastatur (Windows touch-tastatur).
/// Back + Start samtidig slår av/på.
/// </summary>
public sealed class PadMouse : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput mi;
        [FieldOffset(0)] public KeyInput ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, Input[] inputs, int size);

    private const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, RDOWN = 0x8, RUP = 0x10,
        MDOWN = 0x20, MUP = 0x40, WHEEL = 0x800, KEYUP = 0x2;
    private const ushort VK_BACK = 0xA6, VK_FWD = 0xA7;

    private const int LDead = 7849, RDead = 8689;

    private readonly Thread _thread;
    private volatile bool _run = true;
    private double _ax, _ay, _sy;
    private ushort _prev;
    private bool _comboHeld;

    public volatile bool Enabled;
    /// <summary>Settes mens en strøm pågår (på klienten og på verten). Da gjør Back+Start ingenting og kontrolleren styrer ikke musepekeren,
    /// ellers slås «kontroller som mus» av/på på begge maskinene samtidig.</summary>
    public volatile bool Suspended;
    /// <summary>Kalles på en bakgrunnstråd når Back+Start slår funksjonen av/på.</summary>
    public event Action<bool>? Toggled;

    public PadMouse(bool enabled)
    {
        Enabled = enabled;
        _self = this;
        Log($"PadMouse startet. Enabled={enabled}, prosess={Environment.ProcessId}");
        _thread = new Thread(Loop) { IsBackground = true, Name = "PadMouse" };
        _thread.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public int size, flags; public IntPtr hCursor; public int x, y; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo ci);

    // ---- Diagnoselogg: %LOCALAPPDATA%\ZenStationShell\tools\padmouse.log (kun tilstandsendringer, ingen støy) ----
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenStationShell", "tools", "padmouse.log");
    private static readonly object LogLock = new();
    internal static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private string _lastFg = "";
    private uint _lastXi = uint.MaxValue;
    private int _lastCursorShown = -1;
    private int _sendFails;
    private bool _stickWas;
    private bool _wasRaw;
    private readonly RawPad _raw = new();
    private int _moveLogs;

    private static string ForegroundName(out bool own)
    {
        own = false;
        var h = NativeMethods.GetForegroundWindow();
        if (h == IntPtr.Zero) return "(ingen forgrunnsvindu)";
        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        own = pid == (uint)Environment.ProcessId;
        try { return $"{System.Diagnostics.Process.GetProcessById((int)pid).ProcessName} (pid {pid})"; }
        catch { return $"pid {pid}"; }
    }

    /// <summary>Er ZenLauncher selv i forgrunnen? Ingen forgrunnsvindu (typisk i Xbox mode uten Utforsker) teller IKKE som vårt eget vindu.</summary>
    private static bool OwnWindowFocused()
    {
        var h = NativeMethods.GetForegroundWindow();
        if (h == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private void LogState(uint xi, bool active)
    {
        if (xi != _lastXi) { _lastXi = xi; Log($"XInput(0) status={xi} ({(xi == 0 ? "kontroller funnet" : "ingen kontroller")})"); }
        var fg = ForegroundName(out _);
        if (fg != _lastFg) { _lastFg = fg; Log($"Forgrunn: {fg}   Enabled={Enabled}   aktiv={active}"); }
        if (active)
        {
            var ci = new CursorInfo { size = Marshal.SizeOf<CursorInfo>() };
            int shown = GetCursorInfo(ref ci) ? (ci.flags & 1) : -1;
            if (shown != _lastCursorShown) { _lastCursorShown = shown; Log($"Musepeker synlig (CURSOR_SHOWING)={shown} pos={ci.x},{ci.y}"); }
        }
    }

    private void Loop()
    {
        int delay = 50;
        while (_run)
        {
            Thread.Sleep(delay);
            try { delay = Tick(); } catch { delay = 200; }
        }
    }

    /// <summary>Gjør én avlesing. Returnerer ventetid til neste (8 ms når musemodus brukes, ellers sjeldnere).</summary>
    private int Tick()
    {
        var xi = XInputGetState(0, out var s);
        // I Xbox mode gir XInput nøytral data når en annen app har fokus. Da brukes Raw Input (HID) i stedet.
        bool neutral = xi != 0 || (s.Buttons == 0 && s.LT == 0 && s.RT == 0
            && Math.Abs((int)s.LX) < LDead && Math.Abs((int)s.LY) < LDead && Math.Abs((int)s.RX) < RDead && Math.Abs((int)s.RY) < RDead);
        bool useRaw = false;
        if (neutral && _raw.TryGet(out var rb, out var rlt, out var rrt, out var rlx, out var rly, out var rrx, out var rry))
        {
            s.Buttons = rb; s.LT = rlt; s.RT = rrt; s.LX = rlx; s.LY = rly; s.RX = rrx; s.RY = rry;
            useRaw = true;
        }
        if (useRaw != _wasRaw) { _wasRaw = useRaw; Log(useRaw ? "Inndata-kilde: Raw Input (XInput gir nøytral)" : "Inndata-kilde: XInput"); }
        if (xi != 0 && !useRaw) { LogState(xi, false); return 1000; } // ingen kontroller: sjekk sjelden

        bool combo = (s.Buttons & 0x30) == 0x30; // Back + Start
        if (combo && !_comboHeld && !Suspended) { Enabled = !Enabled; Toggled?.Invoke(Enabled); }
        _comboHeld = combo;

        if (s.Buttons != _prev) Log($"Rå XInput: knapper=0x{s.Buttons:X4} (før 0x{_prev:X4}) forgrunn={_lastFg}");
        bool stickOn = Math.Abs((int)s.LX) > LDead || Math.Abs((int)s.LY) > LDead;
        if (stickOn != _stickWas) { _stickWas = stickOn; Log($"Venstrestikk {(stickOn ? "beveges" : "sluppet")} LX={s.LX} LY={s.LY} Enabled={Enabled}"); }
        var pressed = (ushort)(s.Buttons & ~_prev);
        var released = (ushort)(~s.Buttons & _prev);
        _prev = s.Buttons;

        bool active = Enabled && !Suspended && !OwnWindowFocused();
        LogState(xi, active);
        if (!active) { _ax = _ay = _sy = 0; return 50; }
        if (combo) return 8;

        // Knapper
        if ((pressed & 0x1000) != 0) Mouse(LDOWN);
        if ((released & 0x1000) != 0) Mouse(LUP);
        if ((pressed & 0x2000) != 0) Mouse(RDOWN);
        if ((released & 0x2000) != 0) Mouse(RUP);
        if ((pressed & 0x80) != 0) Mouse(MDOWN);
        if ((released & 0x80) != 0) Mouse(MUP);
        if ((pressed & 0x100) != 0) Key(VK_BACK);
        if ((pressed & 0x200) != 0) Key(VK_FWD);
        if ((pressed & 0x8000) != 0) { Log("Y trykket -> touch-tastatur"); TouchKeyboard.Toggle(); }
        if ((pressed & 0x1000) != 0) Log("A trykket -> venstreklikk");

        // Peker
        Axis(s.LX, s.LY, LDead, out var nx, out var ny);
        double speed = 14;
        if (s.LT > 30) speed = 4;
        else if (s.RT > 30) speed = 34;
        // Lett akselerasjonskurve gir presisjon nær midten.
        double mag = Math.Sqrt(nx * nx + ny * ny);
        double k = speed * mag; // mag er 0..1, ganger retning gir mag^2
        _ax += nx * k;
        _ay += -ny * k;
        int mx = (int)_ax, my = (int)_ay;
        _ax -= mx; _ay -= my;
        if (mx != 0 || my != 0)
        {
            var before = new CursorInfo { size = Marshal.SizeOf<CursorInfo>() };
            GetCursorInfo(ref before);
            Move(mx, my);
            if (_moveLogs++ < 6)
            {
                var after = new CursorInfo { size = Marshal.SizeOf<CursorInfo>() };
                GetCursorInfo(ref after);
                Log($"Flytt dx={mx} dy={my}: peker {before.x},{before.y} -> {after.x},{after.y}");
            }
        }

        // Rulling
        Axis(s.RX, s.RY, RDead, out _, out var sy);
        _sy += sy * 28;
        int notch = (int)_sy;
        if (notch != 0)
        {
            _sy -= notch;
            Send(new Input { type = 0, u = { mi = { flags = WHEEL, data = unchecked((uint)notch) } } });
        }
        return 8;
    }

    private static void Axis(short x, short y, int dead, out double nx, out double ny)
    {
        double mag = Math.Sqrt((double)x * x + (double)y * y);
        if (mag < dead) { nx = ny = 0; return; }
        double n = Math.Min((mag - dead) / (32767 - dead), 1);
        nx = x / mag * n;
        ny = y / mag * n;
    }

    private static void Move(int dx, int dy) =>
        Send(new Input { type = 0, u = { mi = { dx = dx, dy = dy, flags = MOVE } } });

    private static void Mouse(uint flag) =>
        Send(new Input { type = 0, u = { mi = { flags = flag } } });

    private static void Key(ushort vk)
    {
        Send(new Input { type = 1, u = { ki = { vk = vk } } });
        Send(new Input { type = 1, u = { ki = { vk = vk, flags = KEYUP } } });
    }

    private static PadMouse? _self;
    private static void Send(Input i)
    {
        var n = SendInput(1, new[] { i }, Marshal.SizeOf<Input>());
        if (n == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (_self != null && _self._sendFails++ < 5) Log($"SendInput FEILET, Win32-feil {err} (5 = blokkert, typisk høyere rettighetsnivå)");
        }
    }

    public void Dispose() => _run = false;
}
