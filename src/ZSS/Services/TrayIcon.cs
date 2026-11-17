using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

/// <summary>
/// Ikon i systemkurven (ved klokken). WinUI 3 har ingen innebygd, så vi bruker Shell_NotifyIcon med et eget skjult meldingsvindu på en egen tråd.
/// Venstreklikk åpner appen, høyreklikk gir meny (Åpne / Avslutt).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public event Action? Open;
    public event Action? Exit;

    private const uint WM_APP_TRAY = 0x8000 + 1;
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_NULL = 0;
    private const uint NIM_ADD = 0, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style; public IntPtr WndProc; public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size; public IntPtr Hwnd; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Item; public IntPtr BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg m);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIconW(IntPtr inst, string file, uint index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint msg, ref NotifyIconData data);

    private readonly WndProcDelegate _proc;
    private IntPtr _hwnd;
    private uint _taskbarCreated;
    private readonly string _tip;
    private IntPtr _icon;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _disposed;

    public TrayIcon(string tip)
    {
        _tip = tip;
        _proc = WndProc;
        var t = new Thread(Run) { IsBackground = true, Name = "TrayIcon" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        _ready.Wait(2000);
    }

    private void Run()
    {
        try
        {
            var inst = GetModuleHandleW(null);
            var cls = "ZSSTrayWindow";
            var wc = new WndClass { WndProc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = inst, ClassName = cls };
            RegisterClassW(ref wc);
            _hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, inst, IntPtr.Zero);
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _icon = IntPtr.Zero;
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    var h = ExtractIconW(inst, exe, 0);
                    if (h.ToInt64() > 1) _icon = h;
                }
            }
            catch { }
            if (_icon == IntPtr.Zero) _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // standard programikon
            AddIcon();
            _ready.Set();
            while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
        }
        catch { _ready.Set(); }
    }

    private NotifyIconData Data() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(), Hwnd = _hwnd, Id = 1, Flags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        CallbackMessage = WM_APP_TRAY, Icon = _icon, Tip = _tip, Info = "", InfoTitle = "",
    };

    private void AddIcon() { var d = Data(); Shell_NotifyIconW(NIM_ADD, ref d); }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) Open?.Invoke();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0) { AddIcon(); return IntPtr.Zero; } // Utforsker startet på nytt
        if (msg == WM_CLOSE) { DestroyWindow(hWnd); return IntPtr.Zero; }
        if (msg == WM_DESTROY)
        {
            var d = Data(); Shell_NotifyIconW(NIM_DELETE, ref d);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (UIntPtr)1, "Åpne ZenLauncher");
        AppendMenuW(menu, 0, (UIntPtr)2, "Avslutt");
        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd); // nødvendig for at menyen skal lukkes ved klikk utenfor
        int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 1) Open?.Invoke();
        else if (cmd == 2) Exit?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
}
