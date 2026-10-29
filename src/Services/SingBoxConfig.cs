using System.Net;
using System.Text.Json;

namespace MehrVpn.Services;

public static class SingBoxConfig
{
    public const string TunName = "MehrTun";

    /// <param name="excludeIps">office OpenVPN server addresses that must bypass the TUN</param>
    /// <param name="ovpnInterface">network adapter name of the OpenVPN tunnel, or null</param>
    public static string Build(AppSettings s, ServerAccount server, IList<string> excludeIps, string ovpnInterface)
    {
        var parsed = LinkParser.Parse(server.Link);
        var proxy = parsed.Outbound;
        proxy["tag"] = "proxy";

        bool tun = s.Mode != RouteMode.SystemProxy;
        bool all = s.Mode == RouteMode.AllApps;

        // ---- process names (original case and lower case, to be safe)
        var procNames = new List<string>();
        foreach (var a in s.ProxyApps)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            string n = a.Trim();
            if (!procNames.Contains(n)) procNames.Add(n);
            string l = n.ToLowerInvariant();
            if (!procNames.Contains(l)) procNames.Add(l);
        }

        // ---- DNS
        var dnsRules = new List<object>();
        if (tun && !all && procNames.Count > 0)
            dnsRules.Add(new Dictionary<string, object>
            {
                ["process_name"] = procNames,
                ["server"] = "dns-remote"
            });

        var dns = new Dictionary<string, object>
        {
            ["servers"] = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "local", ["tag"] = "dns-local" },
                new Dictionary<string, object>
                {
                    ["type"] = "tcp",
                    ["tag"] = "dns-remote",
                    ["server"] = "1.1.1.1",
                    ["detour"] = "proxy"
                }
            },
            ["rules"] = dnsRules,
            ["final"] = all ? "dns-remote" : "dns-local",
            ["strategy"] = "ipv4_only"
        };

        // ---- inbounds
        var inbounds = new List<object>();
        if (tun)
        {
            var tunIn = new Dictionary<string, object>
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunName,
                ["address"] = new List<string> { "172.19.0.1/30" },
                ["mtu"] = 1400,
                ["auto_route"] = true,
                ["strict_route"] = false,
                ["stack"] = "mixed"
            };
            if (excludeIps != null && excludeIps.Count > 0)
            {
                var ex = new List<string>();
                foreach (var ip in excludeIps)
                    if (IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ex.Add(ip + "/32");
                if (ex.Count > 0) tunIn["route_exclude_address"] = ex;
            }
            inbounds.Add(tunIn);
        }
        else
        {
            inbounds.Add(new Dictionary<string, object>
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = s.ProxyPort
            });
        }

        // ---- outbounds
        var outbounds = new List<object>
        {
            proxy,
            new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" }
        };

        // ---- route
        var rules = new List<object>
        {
            new Dictionary<string, object> { ["action"] = "sniff" },
            new Dictionary<string, object> { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };

        if (tun)
        {
            // OpenVPN and sing-box themselves, and local/private networks, never go through the proxy
            rules.Add(new Dictionary<string, object>
            {
                ["process_name"] = new List<string> { "openvpn.exe", "sing-box.exe", "MehrVpn.exe" },
                ["outbound"] = "direct"
            });
            rules.Add(new Dictionary<string, object>
            {
                ["ip_is_private"] = true,
                ["outbound"] = "direct"
            });
            if (!all && procNames.Count > 0)
                rules.Add(new Dictionary<string, object>
                {
                    ["process_name"] = procNames,
                    ["outbound"] = "proxy"
                });
        }

        var route = new Dictionary<string, object>
        {
            ["rules"] = rules,
            ["final"] = (all || !tun) ? "proxy" : "direct",
            ["find_process"] = tun,
            ["default_domain_resolver"] = "dns-local"
        };
        if (tun)
        {
            if (!string.IsNullOrEmpty(ovpnInterface))
                route["default_interface"] = ovpnInterface;
            else
                route["auto_detect_interface"] = true;
        }

        var root = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object> { ["level"] = "info", ["timestamp"] = true },
            ["dns"] = dns,
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = route
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }
}
