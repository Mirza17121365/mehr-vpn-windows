using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MehrVpn.Services;

public static class NetTools
{
    /// <summary>Finds the adapter used by the OpenVPN tunnel (Wintun / TAP / DCO).</summary>
    public static string FindOpenVpnInterface()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.Name == SingBoxConfig.TunName) continue;
                string d = (ni.Description ?? "").ToLowerInvariant();
                string n = (ni.Name ?? "").ToLowerInvariant();
                if (d.Contains("tap-windows") || d.Contains("openvpn") ||
                    d.Contains("wintun") || d.Contains("data channel offload") ||
                    n.Contains("openvpn"))
                    return ni.Name;
            }
        }
        catch { }
        return null;
    }

    public static async Task<List<string>> ResolveAsync(IEnumerable<string> hosts)
    {
        var result = new List<string>();
        foreach (var h in hosts)
        {
            try
            {
                if (IPAddress.TryParse(h, out var ip))
                {
                    result.Add(ip.ToString());
                    continue;
                }
                var addrs = await Dns.GetHostAddressesAsync(h);
                foreach (var a in addrs)
                    if (a.AddressFamily == AddressFamily.InterNetwork && !result.Contains(a.ToString()))
                        result.Add(a.ToString());
            }
            catch (Exception ex)
            {
                Logger.Write("net", "resolve failed for " + h + ": " + ex.Message);
            }
        }
        return result;
    }

    public static async Task<int> TcpPingAsync(string host, int port, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (done != connect) return -1;
            await connect;   // propagate errors
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public static async Task<string> RunDiagnosticsAsync()
    {
        var sb = new StringBuilder();
        foreach (var cmd in new[] { "route print -4", "ipconfig /all" })
        {
            try
            {
                var parts = cmd.Split(' ', 2);
                var psi = new ProcessStartInfo
                {
                    FileName = parts[0],
                    Arguments = parts[1],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                string o = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                sb.AppendLine("==== " + cmd + " ====");
                sb.AppendLine(o);
            }
            catch (Exception ex)
            {
                sb.AppendLine(cmd + " failed: " + ex.Message);
            }
        }
        return sb.ToString();
    }
}

public static class SystemProxy
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    static string BackupPath => Path.Combine(SettingsStore.DataDir, "proxy-backup.txt");

    public static void Enable(int port)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, true);
        if (k == null) return;

        if (!File.Exists(BackupPath))
        {
            string en = Convert.ToString(k.GetValue("ProxyEnable", 0));
            string sv = Convert.ToString(k.GetValue("ProxyServer", ""));
            string ov = Convert.ToString(k.GetValue("ProxyOverride", ""));
            File.WriteAllLines(BackupPath, new[] { en, sv, ov });
        }

        k.SetValue("ProxyServer", "127.0.0.1:" + port);
        k.SetValue("ProxyOverride",
            "localhost;127.*;10.*;192.168.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;" +
            "172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;<local>");
        k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        Refresh();
    }

    public static void Disable()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;

            if (File.Exists(BackupPath))
            {
                var lines = File.ReadAllLines(BackupPath);
                int.TryParse(lines.Length > 0 ? lines[0] : "0", out int en);
                k.SetValue("ProxyEnable", en, RegistryValueKind.DWord);
                k.SetValue("ProxyServer", lines.Length > 1 ? lines[1] : "");
                k.SetValue("ProxyOverride", lines.Length > 2 ? lines[2] : "");
                File.Delete(BackupPath);
            }
            else
            {
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            }
            Refresh();
        }
        catch (Exception ex)
        {
            Logger.Write("proxy", "restore failed: " + ex.Message);
        }
    }

    /// <summary>If the app crashed while the system proxy was on, put the old settings back.</summary>
    public static void RestoreIfLeftover()
    {
        if (File.Exists(BackupPath)) Disable();
    }

    static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);  // SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);  // REFRESH
    }
}
