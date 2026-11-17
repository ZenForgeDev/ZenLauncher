using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ZenStationShell.Services;

/// <summary>
/// Wake-on-LAN: sender «magic packet» (UDP broadcast, port 9) ut på alle nettverkskort. Virker bare på samme lokalnett
/// som verten (ikke over Tailscale/internett), og krever at Wake-on-LAN er slått på i BIOS og nettverkskortet på verten.
/// </summary>
public static class WolService
{
    /// <summary>Gir «AA:BB:CC:DD:EE:FF», eller tom streng hvis teksten ikke er en gyldig MAC-adresse.</summary>
    public static string NormalizeMac(string s)
    {
        var h = Regex.Replace(s ?? "", "[^0-9A-Fa-f]", "");
        if (h.Length != 12) return "";
        return string.Join(":", Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2))).ToUpperInvariant();
    }

    /// <summary>Slår opp MAC-adressen til en IPv4-adresse på lokalnettet via ARP-tabellen. Tom streng hvis den ikke finnes.</summary>
    public static async Task<string> ResolveMacAsync(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return "";
        try
        {
            using (var ping = new Ping()) { try { await ping.SendPingAsync(a, 1500); } catch { } }
            var psi = new ProcessStartInfo("arp", "-a " + ip) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var o = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var m = Regex.Match(o, Regex.Escape(ip) + @"\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})");
            return m.Success ? NormalizeMac(m.Groups[1].Value) : "";
        }
        catch { return ""; }
    }

    /// <summary>Sender magic packet fra alle oppe-kort. Returnerer antall kort den ble sendt fra.</summary>
    public static int Send(string mac)
    {
        var norm = NormalizeMac(mac);
        if (norm.Length == 0) return 0;
        var macBytes = Convert.FromHexString(norm.Replace(":", ""));
        var pkt = new byte[102];
        for (int i = 0; i < 6; i++) pkt[i] = 0xFF;
        for (int i = 0; i < 16; i++) Buffer.BlockCopy(macBytes, 0, pkt, 6 + i * 6, 6);
        int sent = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(ua.Address, 0)) { EnableBroadcast = true };
                    udp.Send(pkt, pkt.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                    sent++;
                }
                catch (Exception ex) { StreamService.Log("WOL " + ua.Address + ": " + ex.Message); }
            }
        }
        StreamService.Log($"WOL {norm}: sendt fra {sent} kort");
        return sent;
    }

    /// <summary>Vekker verten og venter til den svarer (Apollo/Sunshine på port 47989), eller tiden er ute.</summary>
    public static async Task<bool> WakeAndWaitAsync(string addr, string mac, int seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        int round = 0;
        while (DateTime.UtcNow < end)
        {
            if (round++ % 5 == 0) Send(mac); // send på nytt hvert 15. sekund
            await Task.Delay(3000);
            if (await StreamService.ProbeHostAsync(addr) is not null) return true;
        }
        return false;
    }

    /// <summary>Tailscale bruker 100.64.0.0/10.</summary>
    public static bool IsTailscale(string addr)
    {
        if (!IPAddress.TryParse(addr, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }
}
