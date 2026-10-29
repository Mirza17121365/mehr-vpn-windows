using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Web;

namespace MehrVpn.Services;

public class ParsedLink
{
    public string Name;
    public string Protocol;
    public string Host;
    public int Port;
    public Dictionary<string, object> Outbound;   // sing-box outbound (without tag)
}

public static class LinkParser
{
    public static bool LooksLikeLink(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().ToLowerInvariant();
        return s.StartsWith("vless://") || s.StartsWith("vmess://") ||
               s.StartsWith("trojan://") || s.StartsWith("ss://");
    }

    public static ParsedLink Parse(string link)
    {
        link = link.Trim();
        string lower = link.ToLowerInvariant();
        if (lower.StartsWith("vless://")) return ParseVless(link);
        if (lower.StartsWith("vmess://")) return ParseVmess(link);
        if (lower.StartsWith("trojan://")) return ParseTrojan(link);
        if (lower.StartsWith("ss://")) return ParseShadowsocks(link);
        throw new FormatException("پروتکل پشتیبانی نمی‌شود (فقط vless, vmess, trojan, ss)");
    }

    // ---------------------------------------------------------------- vless
    static ParsedLink ParseVless(string link)
    {
        var uri = new Uri(link);
        var q = HttpUtility.ParseQueryString(uri.Query);
        string host = CleanHost(uri.Host);
        int port = uri.Port > 0 ? uri.Port : 443;

        var o = new Dictionary<string, object>
        {
            ["type"] = "vless",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = Uri.UnescapeDataString(uri.UserInfo),
            ["packet_encoding"] = "xudp"
        };
        string flow = q["flow"];
        if (!string.IsNullOrEmpty(flow)) o["flow"] = flow;

        string security = (q["security"] ?? "none").ToLowerInvariant();
        ApplyTls(o, security, host, q["sni"], q["host"], q["alpn"], q["fp"],
                 q["pbk"], q["sid"], q["allowInsecure"] ?? q["insecure"]);
        ApplyTransport(o, (q["type"] ?? "tcp").ToLowerInvariant(),
                       q["host"], q["path"], q["serviceName"]);

        return new ParsedLink
        {
            Name = FragmentName(uri, host + ":" + port),
            Protocol = "vless",
            Host = host,
            Port = port,
            Outbound = o
        };
    }

    // ---------------------------------------------------------------- vmess
    static ParsedLink ParseVmess(string link)
    {
        string b64 = link.Substring("vmess://".Length);
        int hash = b64.IndexOf('#');
        if (hash >= 0) b64 = b64.Substring(0, hash);
        string json = Encoding.UTF8.GetString(DecodeBase64(b64));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        string host = J(r, "add");
        int port = int.TryParse(J(r, "port"), out int p) ? p : 443;
        int aid = int.TryParse(J(r, "aid"), out int a) ? a : 0;
        string scy = J(r, "scy");
        string net = (J(r, "net") ?? "tcp").ToLowerInvariant();
        string tls = (J(r, "tls") ?? "").ToLowerInvariant();

        var o = new Dictionary<string, object>
        {
            ["type"] = "vmess",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = J(r, "id"),
            ["alter_id"] = aid,
            ["security"] = string.IsNullOrEmpty(scy) ? "auto" : scy,
            ["packet_encoding"] = "xudp"
        };

        ApplyTls(o, tls == "tls" ? "tls" : "none", host, J(r, "sni"), J(r, "host"),
                 J(r, "alpn"), J(r, "fp"), null, null, null);
        ApplyTransport(o, net, J(r, "host"), J(r, "path"), J(r, "path"));

        string name = J(r, "ps");
        if (string.IsNullOrWhiteSpace(name)) name = host + ":" + port;
        return new ParsedLink { Name = name, Protocol = "vmess", Host = host, Port = port, Outbound = o };
    }

    // --------------------------------------------------------------- trojan
    static ParsedLink ParseTrojan(string link)
    {
        var uri = new Uri(link);
        var q = HttpUtility.ParseQueryString(uri.Query);
        string host = CleanHost(uri.Host);
        int port = uri.Port > 0 ? uri.Port : 443;

        var o = new Dictionary<string, object>
        {
            ["type"] = "trojan",
            ["server"] = host,
            ["server_port"] = port,
            ["password"] = Uri.UnescapeDataString(uri.UserInfo)
        };
        string security = (q["security"] ?? "tls").ToLowerInvariant();
        ApplyTls(o, security, host, q["sni"] ?? q["peer"], q["host"], q["alpn"], q["fp"],
                 q["pbk"], q["sid"], q["allowInsecure"] ?? q["insecure"]);
        ApplyTransport(o, (q["type"] ?? "tcp").ToLowerInvariant(),
                       q["host"], q["path"], q["serviceName"]);

        return new ParsedLink
        {
            Name = FragmentName(uri, host + ":" + port),
            Protocol = "trojan",
            Host = host,
            Port = port,
            Outbound = o
        };
    }

    // ---------------------------------------------------------- shadowsocks
    static ParsedLink ParseShadowsocks(string link)
    {
        string rest = link.Substring("ss://".Length);
        string name = null;
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(rest.Substring(hash + 1));
            rest = rest.Substring(0, hash);
        }
        int qm = rest.IndexOf('?');
        if (qm >= 0) rest = rest.Substring(0, qm);   // plugin options are ignored
        rest = rest.TrimEnd('/');

        string method, password, host;
        int port;

        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            string userInfo = rest.Substring(0, at);
            string hostPort = rest.Substring(at + 1);
            string decoded;
            if (userInfo.Contains(':'))
                decoded = Uri.UnescapeDataString(userInfo);
            else
                decoded = Encoding.UTF8.GetString(DecodeBase64(Uri.UnescapeDataString(userInfo)));
            SplitOnce(decoded, ':', out method, out password);
            SplitHostPort(hostPort, out host, out port);
        }
        else
        {
            string decoded = Encoding.UTF8.GetString(DecodeBase64(rest));
            int at2 = decoded.LastIndexOf('@');
            if (at2 < 0) throw new FormatException("لینک ss معتبر نیست");
            SplitOnce(decoded.Substring(0, at2), ':', out method, out password);
            SplitHostPort(decoded.Substring(at2 + 1), out host, out port);
        }

        var o = new Dictionary<string, object>
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password
        };
        if (string.IsNullOrWhiteSpace(name)) name = host + ":" + port;
        return new ParsedLink { Name = name, Protocol = "ss", Host = host, Port = port, Outbound = o };
    }

    // -------------------------------------------------------------- helpers
    static void ApplyTls(Dictionary<string, object> o, string security, string server,
                         string sni, string hostHeader, string alpn, string fp,
                         string pbk, string sid, string insecure)
    {
        if (security != "tls" && security != "reality") return;

        var tls = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["server_name"] = !string.IsNullOrEmpty(sni) ? sni
                              : (!string.IsNullOrEmpty(hostHeader) ? hostHeader : server)
        };
        if (!string.IsNullOrEmpty(alpn))
            tls["alpn"] = alpn.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (insecure == "1" || string.Equals(insecure, "true", StringComparison.OrdinalIgnoreCase))
            tls["insecure"] = true;

        if (security == "reality")
        {
            tls["utls"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrEmpty(fp) ? "chrome" : fp
            };
            var reality = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["public_key"] = pbk ?? ""
            };
            if (!string.IsNullOrEmpty(sid)) reality["short_id"] = sid;
            tls["reality"] = reality;
        }
        else if (!string.IsNullOrEmpty(fp) && fp != "none")
        {
            tls["utls"] = new Dictionary<string, object>
            {
                ["enabled"] = true,
                ["fingerprint"] = fp
            };
        }
        o["tls"] = tls;
    }

    static void ApplyTransport(Dictionary<string, object> o, string net,
                               string host, string path, string serviceName)
    {
        switch (net)
        {
            case "ws":
                {
                    var t = new Dictionary<string, object> { ["type"] = "ws" };
                    t["path"] = string.IsNullOrEmpty(path) ? "/" : path;
                    if (!string.IsNullOrEmpty(host))
                        t["headers"] = new Dictionary<string, object> { ["Host"] = host };
                    o["transport"] = t;
                    break;
                }
            case "grpc":
                {
                    o["transport"] = new Dictionary<string, object>
                    {
                        ["type"] = "grpc",
                        ["service_name"] = serviceName ?? ""
                    };
                    break;
                }
            case "http":
            case "h2":
                {
                    var t = new Dictionary<string, object> { ["type"] = "http" };
                    if (!string.IsNullOrEmpty(host)) t["host"] = host.Split(',');
                    t["path"] = string.IsNullOrEmpty(path) ? "/" : path;
                    o["transport"] = t;
                    break;
                }
            case "httpupgrade":
                {
                    var t = new Dictionary<string, object> { ["type"] = "httpupgrade" };
                    if (!string.IsNullOrEmpty(host)) t["host"] = host;
                    t["path"] = string.IsNullOrEmpty(path) ? "/" : path;
                    o["transport"] = t;
                    break;
                }
            case "tcp":
            case "":
            case "none":
                break;
            default:
                throw new FormatException("نوع انتقال «" + net + "» پشتیبانی نمی‌شود");
        }
    }

    static string FragmentName(Uri uri, string fallback)
    {
        string f = uri.Fragment;
        if (string.IsNullOrEmpty(f)) return fallback;
        f = Uri.UnescapeDataString(f.TrimStart('#'));
        return string.IsNullOrWhiteSpace(f) ? fallback : f;
    }

    static string CleanHost(string h)
    {
        if (h.StartsWith("[") && h.EndsWith("]")) return h.Substring(1, h.Length - 2);
        return h;
    }

    static string J(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    static void SplitOnce(string s, char c, out string a, out string b)
    {
        int i = s.IndexOf(c);
        if (i < 0) throw new FormatException("لینک ss معتبر نیست");
        a = s.Substring(0, i);
        b = s.Substring(i + 1);
    }

    static void SplitHostPort(string hp, out string host, out int port)
    {
        hp = hp.Trim().TrimEnd('/');
        int i = hp.LastIndexOf(':');
        if (i < 0) throw new FormatException("آدرس سرور معتبر نیست");
        host = CleanHost(hp.Substring(0, i));
        port = int.Parse(hp.Substring(i + 1));
    }

    public static byte[] DecodeBase64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
