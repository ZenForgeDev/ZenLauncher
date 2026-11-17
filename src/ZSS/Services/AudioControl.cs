using System.Runtime.InteropServices;

namespace ZenStationShell.Services;

/// <summary>Systemvolum, demping og valg av lydutgang via Windows Core Audio (+ den udokumenterte IPolicyConfig).</summary>
internal static class AudioControl
{
    public sealed record Device(string Id, string Name, bool IsDefault);

    // ── COM ────────────────────────────────────────────────────────────────
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] private class PolicyConfigClient { }

    // Udokumentert; rekkefølgen på vtable-plassene er viktig (SetDefaultEndpoint er nr. 11 etter IUnknown).
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int NotImpl1();
        [PreserveSig] int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl (må stå først, i denne rekkefølgen)
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName();
        [PreserveSig] int GetIconPath();
        [PreserveSig] int SetIconPath();
        [PreserveSig] int GetGroupingParam();
        [PreserveSig] int SetGroupingParam();
        [PreserveSig] int RegisterAudioSessionNotification();
        [PreserveSig] int UnregisterAudioSessionNotification();
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    private static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static Guid _ctx = Guid.Empty;

    private static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    private static IAudioEndpointVolume? Volume()
    {
        if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return null;
        var iid = IidEndpointVolume;
        return dev.Activate(ref iid, 23, IntPtr.Zero, out var o) == 0 ? o as IAudioEndpointVolume : null;
    }

    // ── Volum ──────────────────────────────────────────────────────────────
    /// <summary>0–100, eller -1 hvis det ikke gikk å lese.</summary>
    public static int GetVolume()
    {
        try { var v = Volume(); return v is not null && v.GetMasterVolumeLevelScalar(out var f) == 0 ? (int)Math.Round(f * 100) : -1; }
        catch { return -1; }
    }

    public static void SetVolume(int percent)
    {
        try { Volume()?.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref _ctx); } catch { }
    }

    public static bool GetMute()
    {
        try { var v = Volume(); return v is not null && v.GetMute(out var m) == 0 && m; } catch { return false; }
    }

    public static void SetMute(bool mute)
    {
        try { Volume()?.SetMute(mute, ref _ctx); } catch { }
    }


    // ── Appvolum ───────────────────────────────────────────────────────────
    /// <summary>Én app (alle lyd-øktene til samme prosessnavn samlet, f.eks. alle Chrome-prosesser).</summary>
    public sealed class AppAudio
    {
        internal List<ISimpleAudioVolume> Sessions { get; } = new();
        public string Name { get; internal set; } = "";

        public int Volume
        {
            get { try { return Sessions[0].GetMasterVolume(out var f) == 0 ? (int)Math.Round(f * 100) : 0; } catch { return 0; } }
            set { foreach (var v in Sessions) { try { v.SetMasterVolume(Math.Clamp(value, 0, 100) / 100f, ref _ctx); } catch { } } }
        }

        public bool Muted
        {
            get { try { return Sessions[0].GetMute(out var m) == 0 && m; } catch { return false; } }
            set { foreach (var v in Sessions) { try { v.SetMute(value, ref _ctx); } catch { } } }
        }
    }

    /// <summary>Apper som har en lyd-økt på standard lydutgang (spill, nettleser, osv.).</summary>
    public static List<AppAudio> Apps()
    {
        var result = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return new();
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0 || o is not IAudioSessionManager2 mgr) return new();
            if (mgr.GetSessionEnumerator(out var en) != 0) return new();
            en.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    if (en.GetSession(i, out var so) != 0) continue;
                    if (so is not IAudioSessionControl2 c2 || so is not ISimpleAudioVolume vol) continue;
                    if (c2.GetProcessId(out var pid) != 0 || pid == 0 || pid == (uint)Environment.ProcessId) continue;
                    c2.GetState(out var state);
                    if (state == 2) continue; // utløpt

                    string name;
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                        name = p.ProcessName;
                    }
                    catch { continue; }

                    if (!result.TryGetValue(name, out var app))
                        result[name] = app = new AppAudio { Name = name };
                    app.Sessions.Add(vol);
                }
                catch { }
            }
        }
        catch { }
        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Setter volumet på alle lyd-økter der prosessens exe-sti inneholder markøren (spillets mappe).
    /// Samme økt-oppslag som Apps(). Returnerer antall økter som ble endret (0 hvis spillet ikke har lyd-økt ennå).
    /// </summary>
    public static int SetDirVolume(string marker, int percent)
    {
        int changed = 0;
        try
        {
            if (Enumerator().GetDefaultAudioEndpoint(0, 1, out var dev) != 0) return 0;
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out var o) != 0 || o is not IAudioSessionManager2 mgr) return 0;
            if (mgr.GetSessionEnumerator(out var en) != 0) return 0;
            en.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    if (en.GetSession(i, out var so) != 0) continue;
                    if (so is not IAudioSessionControl2 c2 || so is not ISimpleAudioVolume vol) continue;
                    if (c2.GetProcessId(out var pid) != 0 || pid == 0 || pid == (uint)Environment.ProcessId) continue;
                    var path = NativeMethods.GetProcessPath((int)pid);
                    if (path is null || !path.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                    if (vol.SetMasterVolume(Math.Clamp(percent, 0, 100) / 100f, ref _ctx) == 0) changed++;
                }
                catch { }
            }
        }
        catch { }
        return changed;
    }

    // ── Enheter ────────────────────────────────────────────────────────────
    public static List<Device> Outputs()
    {
        var list = new List<Device>();
        try
        {
            var en = Enumerator();
            string? defId = null;
            if (en.GetDefaultAudioEndpoint(0, 1, out var def) == 0) def.GetId(out defId);

            if (en.EnumAudioEndpoints(0, 1, out var col) != 0) return list; // eRender, DEVICE_STATE_ACTIVE
            col.GetCount(out var n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                dev.GetId(out var id);
                list.Add(new Device(id, NameOf(dev), id == defId));
            }
        }
        catch { }
        return list;
    }

    private static string NameOf(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var store) != 0) return "Ukjent enhet";
            var key = new PropertyKey { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return "Ukjent enhet";
            var name = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            return string.IsNullOrWhiteSpace(name) ? "Ukjent enhet" : name;
        }
        catch { return "Ukjent enhet"; }
    }

    public static bool SetDefault(string deviceId)
    {
        try
        {
            var pc = (IPolicyConfig)new PolicyConfigClient();
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(deviceId, role) == 0;
            return ok;
        }
        catch { return false; }
    }
}
