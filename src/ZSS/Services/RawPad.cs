using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

/// <summary>
/// Reserveleser for kontrolleren via Raw Input (HID). Brukes av PadMouse når XInput bare gir nøytral data,
/// som i Xbox mode når en annen app har fokus. Oversetter HID-rapporten til samme bitmaske som XInput.
/// </summary>
internal sealed class RawPad
{
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const int HidpSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style; public IntPtr WndProc; public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WndClass wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref Msg msg);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRaw, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usageList,
        ref uint usageLength, IntPtr preparsed, byte[] report, uint reportLength);
    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, byte[] report, uint reportLength);

    private readonly Dictionary<IntPtr, IntPtr> _preparsed = new();
    private readonly WndProcDelegate _proc;
    private readonly object _lock = new();
    private ushort _buttons; private byte _lt, _rt; private short _lx, _ly, _rx, _ry;
    private bool _wide;
    private int _logged;

    public volatile bool Seen;

    public RawPad()
    {
        _proc = WndProc;
        var t = new Thread(Run) { IsBackground = true, Name = "RawPad" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public bool TryGet(out ushort buttons, out byte lt, out byte rt, out short lx, out short ly, out short rx, out short ry)
    {
        lock (_lock) { buttons = _buttons; lt = _lt; rt = _rt; lx = _lx; ly = _ly; rx = _rx; ry = _ry; }
        return Seen;
    }

    private void Run()
    {
        try
        {
            var wc = new WndClass { WndProc = Marshal.GetFunctionPointerForDelegate(_proc), Instance = GetModuleHandleW(null), ClassName = "ZSSRawPad" };
            RegisterClassW(ref wc);
            var hwnd = CreateWindowExW(0, "ZSSRawPad", "ZSSRawPad", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.Instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { PadMouse.Log("RawPad: kunne ikke lage meldingsvindu, feil " + Marshal.GetLastWin32Error()); return; }
            var devs = new[]
            {
                new RawDevice { UsagePage = 1, Usage = 5, Flags = RIDEV_INPUTSINK, Target = hwnd },
                new RawDevice { UsagePage = 1, Usage = 4, Flags = RIDEV_INPUTSINK, Target = hwnd },
            };
            bool ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RawDevice>());
            PadMouse.Log("RawPad: RegisterRawInputDevices " + (ok ? "OK" : "FEILET, feil " + Marshal.GetLastWin32Error()));
            while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
        }
        catch (Exception ex) { PadMouse.Log("RawPad feilet: " + ex.GetType().Name + " " + ex.Message); }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { Handle(lParam); } catch (Exception ex) { if (_logged < 20) { _logged++; PadMouse.Log("RawPad Handle feilet: " + ex.Message); } }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Handle(IntPtr hRaw)
    {
        uint headerSize = 24; // RAWINPUTHEADER på 64-bit
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, headerSize) == uint.MaxValue) return;
            if (Marshal.ReadInt32(buf, 0) != 2) return; // RIM_TYPEHID
            var dev = Marshal.ReadIntPtr(buf, 8);
            int sizeHid = Marshal.ReadInt32(buf, (int)headerSize);
            int count = Marshal.ReadInt32(buf, (int)headerSize + 4);
            if (sizeHid <= 0 || count <= 0) return;
            var report = new byte[sizeHid];
            // Siste rapport i pakken er den nyeste.
            Marshal.Copy(buf + (int)headerSize + 8 + (count - 1) * sizeHid, report, 0, sizeHid);

            if (!_preparsed.TryGetValue(dev, out var pp))
            {
                uint ps = 0;
                GetRawInputDeviceInfoW(dev, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ps);
                pp = Marshal.AllocHGlobal((int)Math.Max(ps, 1));
                GetRawInputDeviceInfoW(dev, RIDI_PREPARSEDDATA, pp, ref ps);
                _preparsed[dev] = pp;
            }
            Parse(report, pp);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool Val(ushort page, ushort usage, IntPtr pp, byte[] rep, out uint v) =>
        HidP_GetUsageValue(0, page, 0, usage, out v, pp, rep, (uint)rep.Length) == HidpSuccess;

    private short Axis(uint v, bool invert)
    {
        int n = _wide ? (int)v - 32768 : ((int)v - 128) * 256;
        if (invert) n = -n;
        return (short)Math.Clamp(n, -32768, 32767);
    }

    private void Parse(byte[] rep, IntPtr pp)
    {
        // Knapper (side 9)
        var usages = new ushort[40];
        uint len = (uint)usages.Length;
        ushort b = 0;
        if (HidP_GetUsages(0, 9, 0, usages, ref len, pp, rep, (uint)rep.Length) == HidpSuccess)
        {
            for (int i = 0; i < len; i++)
                b |= usages[i] switch
                {
                    1 => (ushort)0x1000, 2 => (ushort)0x2000, 3 => (ushort)0x4000, 4 => (ushort)0x8000,
                    5 => (ushort)0x0100, 6 => (ushort)0x0200, 7 => (ushort)0x0020, 8 => (ushort)0x0010,
                    9 => (ushort)0x0040, 10 => (ushort)0x0080, _ => (ushort)0
                };
        }
        // Retningskryss (hat)
        if (Val(1, 0x39, pp, rep, out var hat) && hat >= 1 && hat <= 8)
        {
            if (hat == 8 || hat == 1 || hat == 2) b |= 0x0001; // opp
            if (hat >= 2 && hat <= 4) b |= 0x0008;             // høyre
            if (hat >= 4 && hat <= 6) b |= 0x0002;             // ned
            if (hat >= 6 && hat <= 8) b |= 0x0004;             // venstre
        }

        bool hx = Val(1, 0x30, pp, rep, out var x);
        bool hy = Val(1, 0x31, pp, rep, out var y);
        bool hrx = Val(1, 0x33, pp, rep, out var rx);
        bool hry = Val(1, 0x34, pp, rep, out var ry);
        bool hz = Val(1, 0x32, pp, rep, out var z);
        bool hrz = Val(1, 0x35, pp, rep, out var rz);
        // Xbox One-oppsett: høyre stikk = Z/Rz. Eldre oppsett: Rx/Ry.
        uint rxv = 0, ryv = 0; bool hasR = false;
        if (hrx && hry) { rxv = rx; ryv = ry; hasR = true; }
        else if (hz && hrz) { rxv = z; ryv = rz; hasR = true; }
        if (x > 255 || y > 255 || rxv > 255 || ryv > 255) _wide = true;

        uint lt = 0, rt = 0;
        bool haveTrig = Val(2, 0xC5, pp, rep, out lt);
        haveTrig |= Val(2, 0xC4, pp, rep, out rt);

        lock (_lock)
        {
            _buttons = b;
            if (hx && hy) { _lx = Axis(x, false); _ly = Axis(y, true); }
            if (hasR) { _rx = Axis(rxv, false); _ry = Axis(ryv, true); }
            if (haveTrig) { _lt = (byte)Math.Min(255, lt * 255 / 1023); _rt = (byte)Math.Min(255, rt * 255 / 1023); }
        }
        Seen = true;

        if (_logged < 12)
        {
            _logged++;
            PadMouse.Log($"RawPad rapport ({rep.Length} B): {BitConverter.ToString(rep, 0, Math.Min(rep.Length, 20))} -> knapper=0x{b:X4} LX={_lx} LY={_ly} RX={_rx} RY={_ry} LT={_lt} RT={_rt} bred={_wide}");
        }
    }
}
