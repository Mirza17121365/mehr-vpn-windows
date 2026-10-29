using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MehrVpn;

public enum RouteMode
{
    PerApp = 0,       // TUN: only selected apps go through v2ray
    AllApps = 1,      // TUN: everything goes through v2ray
    SystemProxy = 2   // no TUN: local proxy + Windows system proxy
}

public class ServerAccount : INotifyPropertyChanged
{
    string _name;
    int _pingMs = -1;
    string _pingText = "—";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Link { get; set; }
    public string Protocol { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(); }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int PingMs
    {
        get => _pingMs;
        set
        {
            _pingMs = value;
            PingText = value < 0 ? "timeout" : value + " ms";
            OnChanged();
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string PingText
    {
        get => _pingText;
        set { _pingText = value; OnChanged(); }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Address => Host + ":" + Port;

    public event PropertyChangedEventHandler PropertyChanged;
    void OnChanged([CallerMemberName] string n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class AppSettings
{
    public List<ServerAccount> Servers { get; set; } = new();
    public string SelectedServerId { get; set; }
    public List<string> ProxyApps { get; set; } = new();
    public RouteMode Mode { get; set; } = RouteMode.PerApp;
    public int ProxyPort { get; set; } = 2080;
    public string OpenVpnUser { get; set; }
    public string OpenVpnPasswordProtected { get; set; }  // DPAPI, base64
    public string CustomOpenVpnPath { get; set; }
    public bool ConnectOpenVpn { get; set; } = true;
}
