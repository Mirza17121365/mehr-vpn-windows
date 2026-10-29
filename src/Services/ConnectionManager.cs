namespace MehrVpn.Services;

public enum ConnState
{
    Disconnected,
    ConnectingOpenVpn,
    ConnectingV2ray,
    Connected,
    Disconnecting
}

public class ConnectionManager
{
    readonly OpenVpnController _ovpn = new();
    readonly SingBoxController _sb = new();
    readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Settings { get; }
    public ConnState State { get; private set; } = ConnState.Disconnected;
    public bool OpenVpnUp { get; private set; }
    public bool V2rayUp { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public long BytesIn { get; private set; }
    public long BytesOut { get; private set; }

    public event Action Changed;
    public event Action<string> Failed;

    public ConnectionManager(AppSettings settings)
    {
        Settings = settings;
        _ovpn.Bytes += (i, o) => { BytesIn = i; BytesOut = o; Changed?.Invoke(); };
        _ovpn.Exited += () => _ = OnChildExitedAsync("OpenVPN");
        _sb.Exited += () => _ = OnChildExitedAsync("sing-box");
    }

    public bool IsBusy => State != ConnState.Disconnected && State != ConnState.Connected;

    void Set(ConnState s)
    {
        State = s;
        Changed?.Invoke();
    }

    public ServerAccount SelectedServer =>
        Settings.Servers.FirstOrDefault(x => x.Id == Settings.SelectedServerId);

    async Task OnChildExitedAsync(string who)
    {
        if (State != ConnState.Connected) return;
        Logger.Write("app", who + " exited unexpectedly");
        await DisconnectAsync();
        Failed?.Invoke(who + " به‌طور ناگهانی بسته شد. لاگ را ببینید.");
    }

    public async Task ConnectAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            if (State != ConnState.Disconnected) return;
            BytesIn = BytesOut = 0;
            OpenVpnUp = V2rayUp = false;

            var server = SelectedServer;
            string profile = SettingsStore.ProfilePath;
            string ovpnExe = null;
            string sbExe = null;
            List<string> remoteIps = new();

            if (!Settings.ConnectOpenVpn && server == null)
                throw new Exception("هیچ کاری برای انجام نیست: OpenVPN غیرفعال است و سروری انتخاب نشده.");

            // ---------- OpenVPN
            if (Settings.ConnectOpenVpn)
            {
                if (!File.Exists(profile))
                    throw new Exception("پروفایل OpenVPN وارد نشده. از صفحه‌ی تنظیمات فایل .ovpn را انتخاب کنید.");
                ovpnExe = OpenVpnController.FindExe(Settings.CustomOpenVpnPath);
                if (ovpnExe == null)
                    throw new Exception("OpenVPN نصب نیست. در تنظیمات دکمه‌ی «نصب OpenVPN» را بزنید.");

                string user = Settings.OpenVpnUser;
                string pass = SettingsStore.Unprotect(Settings.OpenVpnPasswordProtected);
                if (OpenVpnController.ProfileNeedsCredentials(profile) && string.IsNullOrEmpty(user))
                    throw new Exception("این پروفایل نام کاربری و رمز می‌خواهد. در تنظیمات واردش کنید.");

                remoteIps = await NetTools.ResolveAsync(OpenVpnController.ReadRemoteHosts(profile));
                Logger.Write("app", "office server ips: " + string.Join(", ", remoteIps));

                Set(ConnState.ConnectingOpenVpn);
                await _ovpn.StartAsync(ovpnExe, profile, user, pass);
                OpenVpnUp = true;
                ConnectedAt = DateTime.Now;
                Changed?.Invoke();
            }

            // ---------- v2ray (sing-box)
            if (server != null)
            {
                sbExe = SingBoxController.FindExe();
                if (sbExe == null)
                    throw new Exception("فایل sing-box.exe کنار برنامه پیدا نشد (پوشه‌ی deps\\singbox).");

                Set(ConnState.ConnectingV2ray);

                // give the OpenVPN adapter a moment to settle so it can be found
                await Task.Delay(1500);
                string iface = Settings.ConnectOpenVpn ? NetTools.FindOpenVpnInterface() : null;
                Logger.Write("app", "openvpn interface: " + (iface ?? "(not found, auto detect)"));

                string json = SingBoxConfig.Build(Settings, server, remoteIps, iface);
                File.WriteAllText(SettingsStore.SingBoxConfigPath, json);

                await _sb.CheckConfigAsync(sbExe, SettingsStore.SingBoxConfigPath);
                await _sb.StartAsync(sbExe, SettingsStore.SingBoxConfigPath);

                if (Settings.Mode == RouteMode.SystemProxy)
                    SystemProxy.Enable(Settings.ProxyPort);
                V2rayUp = true;
                ConnectedAt ??= DateTime.Now;
            }

            Set(ConnState.Connected);
        }
        catch (Exception ex)
        {
            Logger.Write("app", "connect failed: " + ex.Message);
            await CleanupAsync();
            Set(ConnState.Disconnected);
            Failed?.Invoke(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == ConnState.Disconnected) return;
            Set(ConnState.Disconnecting);
            await CleanupAsync();
            Set(ConnState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task CleanupAsync()
    {
        try { SystemProxy.Disable(); } catch { }
        try { await _sb.StopAsync(); } catch { }
        try { await _ovpn.StopAsync(); } catch { }
        OpenVpnUp = V2rayUp = false;
        ConnectedAt = null;
    }

    /// <summary>Synchronous shutdown for application exit.</summary>
    public void ShutdownBlocking()
    {
        try { Task.Run(async () => await DisconnectAsync()).Wait(15000); } catch { }
    }
}
