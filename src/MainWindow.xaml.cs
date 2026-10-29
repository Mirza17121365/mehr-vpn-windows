using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using MehrVpn.Services;

namespace MehrVpn;

public partial class MainWindow : Window
{
    readonly AppSettings _s;
    readonly ConnectionManager _cm;
    readonly ObservableCollection<ServerAccount> _servers = new();
    readonly ObservableCollection<string> _apps = new();
    List<string> _runningAll = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    long _lastIn, _lastOut;
    bool _loading = true;

    static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x92));
    static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x1F));
    static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD1, 0x7A));

    public MainWindow()
    {
        InitializeComponent();

        PidFile.KillLeftovers();
        SystemProxy.RestoreIfLeftover();

        _s = SettingsStore.Load();
        foreach (var sv in _s.Servers) _servers.Add(sv);
        foreach (var a in _s.ProxyApps) _apps.Add(a);

        ServerList.ItemsSource = _servers;
        ProxyAppsList.ItemsSource = _apps;
        ServerList.SelectedItem = _servers.FirstOrDefault(x => x.Id == _s.SelectedServerId);

        ModePerApp.IsChecked = _s.Mode == RouteMode.PerApp;
        ModeAll.IsChecked = _s.Mode == RouteMode.AllApps;
        ModeProxy.IsChecked = _s.Mode == RouteMode.SystemProxy;
        ChkOvpn.IsChecked = _s.ConnectOpenVpn;
        UserBox.Text = _s.OpenVpnUser ?? "";
        PortBox.Text = _s.ProxyPort.ToString();
        DataDirText.Text = SettingsStore.DataDir;

        _cm = new ConnectionManager(_s);
        _cm.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateUi));
        _cm.Failed += msg => Dispatcher.BeginInvoke(new Action(() =>
        {
            MessageBox.Show(this, msg + "\n\nبرای جزئیات به صفحه‌ی «لاگ» بروید.", "Mehr VPN",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }));

        Logger.LineAdded += line => Dispatcher.BeginInvoke(new Action(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.LineCount > 3000) LogBox.Clear();
            LogBox.ScrollToEnd();
        }));
        LogBox.Text = Logger.All();

        _timer.Tick += Timer_Tick;
        _timer.Start();

        Closing += MainWindow_Closing;
        SessionEnding += (s, e) => _cm.ShutdownBlocking();

        _loading = false;
        RefreshSettingsInfo();
        RefreshRunning();
        UpdateUi();
        Logger.Write("app", "Mehr VPN started");
    }

    // ================================================================= navigation
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome == null || PageLogs == null) return;
        string tag = (sender as RadioButton)?.Tag as string;
        PageHome.Visibility = tag == "Home" ? Visibility.Visible : Visibility.Collapsed;
        PageServers.Visibility = tag == "Servers" ? Visibility.Visible : Visibility.Collapsed;
        PageApps.Visibility = tag == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = tag == "Logs" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Apps") RefreshRunning();
        if (tag == "Settings") RefreshSettingsInfo();
        if (tag == "Logs") LogBox.ScrollToEnd();
    }

    void SaveAll()
    {
        _s.Servers = _servers.ToList();
        _s.ProxyApps = _apps.ToList();
        SettingsStore.Save(_s);
    }

    // ================================================================= home / status
    async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_cm.State == ConnState.Disconnected)
        {
            SaveAll();
            await _cm.ConnectAsync();
        }
        else if (_cm.State == ConnState.Connected)
        {
            await _cm.DisconnectAsync();
        }
    }

    void UpdateUi()
    {
        var st = _cm.State;
        string text;
        Brush ring;
        switch (st)
        {
            case ConnState.ConnectingOpenVpn: text = "در حال اتصال به OpenVPN…"; ring = AccentBrush; break;
            case ConnState.ConnectingV2ray: text = "در حال راه‌اندازی v2ray…"; ring = AccentBrush; break;
            case ConnState.Connected: text = "متصل"; ring = OkBrush; break;
            case ConnState.Disconnecting: text = "در حال قطع…"; ring = AccentBrush; break;
            default: text = "قطع"; ring = MutedBrush; break;
        }
        StatusText.Text = text;
        PowerButton.BorderBrush = ring;

        if (st == ConnState.ConnectingOpenVpn || st == ConnState.ConnectingV2ray || st == ConnState.Disconnecting)
        {
            var anim = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            PowerButton.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            PowerButton.BeginAnimation(OpacityProperty, null);
            PowerButton.Opacity = 1;
        }

        SetDot(DotOvpn, OvpnState, _cm.OpenVpnUp,
               _cm.OpenVpnUp ? "وصل" : (st == ConnState.ConnectingOpenVpn ? "در حال اتصال" : "قطع"));
        SetDot(DotV2, V2State, _cm.V2rayUp,
               _cm.V2rayUp ? "وصل" : (st == ConnState.ConnectingV2ray ? "در حال اتصال" : "قطع"));

        var sv = _cm.SelectedServer;
        string modeText = _s.Mode == RouteMode.PerApp ? "برنامه‌های انتخابی"
                        : _s.Mode == RouteMode.AllApps ? "همه‌ی برنامه‌ها" : "پراکسی سیستم";
        ServerText.Text = sv == null ? "سرور v2ray انتخاب نشده (فقط OpenVPN)"
                                     : sv.Name + "  ·  " + modeText;
    }

    void SetDot(System.Windows.Shapes.Ellipse dot, TextBlock label, bool up, string text)
    {
        dot.Fill = up ? OkBrush : MutedBrush;
        label.Text = text;
        label.Foreground = up ? OkBrush : MutedBrush;
    }

    void Timer_Tick(object sender, EventArgs e)
    {
        if (_cm.State == ConnState.Connected)
        {
            long dIn = Math.Max(0, _cm.BytesIn - _lastIn);
            long dOut = Math.Max(0, _cm.BytesOut - _lastOut);
            DownText.Text = FormatSpeed(dIn);
            UpText.Text = FormatSpeed(dOut);
            _lastIn = _cm.BytesIn;
            _lastOut = _cm.BytesOut;
            if (_cm.ConnectedAt.HasValue)
                TimeText.Text = (DateTime.Now - _cm.ConnectedAt.Value).ToString(@"hh\:mm\:ss");
        }
        else
        {
            DownText.Text = FormatSpeed(0);
            UpText.Text = FormatSpeed(0);
            TimeText.Text = "00:00:00";
            _lastIn = _lastOut = 0;
        }
    }

    static string FormatSpeed(long bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1024) return kb.ToString("0") + " KB/s";
        return (kb / 1024.0).ToString("0.0") + " MB/s";
    }

    // ================================================================= servers
    void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var sv = ServerList.SelectedItem as ServerAccount;
        _s.SelectedServerId = sv?.Id;
        RenameBox.Text = sv?.Name ?? "";
        SaveAll();
        UpdateUi();
    }

    void AddClipboard_Click(object sender, RoutedEventArgs e)
    {
        string text = null;
        try { text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "کلیپ‌بورد خالی است.", "Mehr VPN");
            return;
        }
        AddLinks(text);
    }

    void AddLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LinkBox.Text)) return;
        if (AddLinks(LinkBox.Text)) LinkBox.Clear();
    }

    bool AddLinks(string text)
    {
        int added = 0;
        var errors = new List<string>();
        var lines = text.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
        {
            if (!LinkParser.LooksLikeLink(l)) continue;
            try
            {
                var p = LinkParser.Parse(l);
                var acc = new ServerAccount
                {
                    Name = p.Name,
                    Link = l.Trim(),
                    Protocol = p.Protocol,
                    Host = p.Host,
                    Port = p.Port
                };
                _servers.Add(acc);
                added++;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        if (added > 0)
        {
            if (ServerList.SelectedItem == null) ServerList.SelectedItem = _servers.Last();
            SaveAll();
        }
        if (added == 0 && errors.Count == 0)
            MessageBox.Show(this, "لینک معتبری پیدا نشد (vless / vmess / trojan / ss).", "Mehr VPN");
        else if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "Mehr VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
        return added > 0;
    }

    void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        var sv = ServerList.SelectedItem as ServerAccount;
        if (sv == null) return;
        _servers.Remove(sv);
        if (_s.SelectedServerId == sv.Id) _s.SelectedServerId = null;
        SaveAll();
        UpdateUi();
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var sv = ServerList.SelectedItem as ServerAccount;
        if (sv == null || string.IsNullOrWhiteSpace(RenameBox.Text)) return;
        sv.Name = RenameBox.Text.Trim();
        SaveAll();
        UpdateUi();
    }

    async void PingAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var sv in _servers) sv.PingText = "…";
        var tasks = _servers.ToList().Select(async sv =>
        {
            int ms = await NetTools.TcpPingAsync(sv.Host, sv.Port);
            sv.PingMs = ms;
        });
        await Task.WhenAll(tasks);
    }

    // ================================================================= apps
    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string tag = (sender as RadioButton)?.Tag as string;
        if (tag == "PerApp") _s.Mode = RouteMode.PerApp;
        else if (tag == "AllApps") _s.Mode = RouteMode.AllApps;
        else if (tag == "SystemProxy") _s.Mode = RouteMode.SystemProxy;
        SaveAll();
        UpdateUi();
    }

    void RefreshRunning()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        names.Add(p.ProcessName + ".exe");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        _runningAll = names.ToList();
        ApplyAppFilter();
    }

    void ApplyAppFilter()
    {
        if (RunningList == null) return;
        string f = (AppFilter.Text ?? "").Trim();
        RunningList.ItemsSource = string.IsNullOrEmpty(f)
            ? _runningAll
            : _runningAll.Where(x => x.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
    }

    void AppFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyAppFilter();
    void RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshRunning();

    void AddApp_Click(object sender, RoutedEventArgs e) => AddAppName(RunningList.SelectedItem as string);
    void RunningList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AddAppName(RunningList.SelectedItem as string);

    void AddAppName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_apps.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _apps.Add(name);
        SaveAll();
    }

    void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        var a = ProxyAppsList.SelectedItem as string;
        if (a == null) return;
        _apps.Remove(a);
        SaveAll();
    }

    void BrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe", Title = "انتخاب برنامه" };
        if (dlg.ShowDialog(this) == true)
            AddAppName(System.IO.Path.GetFileName(dlg.FileName));
    }

    // ================================================================= settings
    void ChkOvpn_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _s.ConnectOpenVpn = ChkOvpn.IsChecked == true;
        SaveAll();
    }

    void PortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(PortBox.Text, out int p) && p > 0 && p < 65536)
        {
            _s.ProxyPort = p;
            SaveAll();
        }
        else
        {
            PortBox.Text = _s.ProxyPort.ToString();
        }
    }

    void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "OpenVPN profile (*.ovpn)|*.ovpn|All files|*.*", Title = "انتخاب پروفایل OpenVPN" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.Copy(dlg.FileName, SettingsStore.ProfilePath, true);
            Logger.Write("app", "profile imported: " + System.IO.Path.GetFileName(dlg.FileName));
            RefreshSettingsInfo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Mehr VPN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void SaveCreds_Click(object sender, RoutedEventArgs e)
    {
        _s.OpenVpnUser = UserBox.Text.Trim();
        _s.OpenVpnPasswordProtected = SettingsStore.Protect(PassBox.Password);
        SaveAll();
        PassBox.Clear();
        MessageBox.Show(this, "ذخیره شد. رمز با رمزنگاری ویندوز (DPAPI) نگه‌داری می‌شود.", "Mehr VPN");
    }

    void RefreshSettingsInfo()
    {
        if (File.Exists(SettingsStore.ProfilePath))
        {
            var hosts = OpenVpnController.ReadRemoteHosts(SettingsStore.ProfilePath);
            string needs = OpenVpnController.ProfileNeedsCredentials(SettingsStore.ProfilePath)
                ? " · نیاز به نام کاربری/رمز" : "";
            ProfileText.Text = "پروفایل وارد شده است · سرور: " +
                (hosts.Count > 0 ? string.Join(", ", hosts) : "؟") + needs;
        }
        else
        {
            ProfileText.Text = "پروفایل وارد نشده است";
        }

        string exe = OpenVpnController.FindExe(_s.CustomOpenVpnPath);
        OvpnExeText.Text = exe == null ? "OpenVPN نصب نیست" : exe;
        InstallOvpnBtn.IsEnabled = exe == null;

        string sb = SingBoxController.FindExe();
        SingBoxText.Text = sb == null ? "sing-box پیدا نشد (پوشه‌ی deps\\singbox کنار برنامه)" : "sing-box: " + sb;
    }

    async void InstallOvpn_Click(object sender, RoutedEventArgs e)
    {
        string msi = System.IO.Path.Combine(AppContext.BaseDirectory, "deps", "openvpn", "openvpn.msi");
        if (!File.Exists(msi))
        {
            MessageBox.Show(this, "فایل نصب کنار برنامه نیست. OpenVPN Community را از سایت رسمی نصب کنید.", "Mehr VPN");
            return;
        }
        InstallOvpnBtn.IsEnabled = false;
        OvpnExeText.Text = "در حال نصب OpenVPN…";
        int code = await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = "/i \"" + msi + "\" /qn /norestart",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p.WaitForExit();
            return p.ExitCode;
        });
        Logger.Write("app", "openvpn msi exit code " + code);
        RefreshSettingsInfo();
        if (OpenVpnController.FindExe(_s.CustomOpenVpnPath) == null)
            MessageBox.Show(this, "نصب انجام نشد (کد " + code + "). لاگ را ببینید.", "Mehr VPN");
    }

    // ================================================================= logs
    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LogBox.Text); } catch { }
    }

    void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        Logger.Clear();
        LogBox.Clear();
    }

    async void Diag_Click(object sender, RoutedEventArgs e)
    {
        string d = await NetTools.RunDiagnosticsAsync();
        Logger.Write("diag", Environment.NewLine + d);
    }

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SettingsStore.DataDir + "\"") { UseShellExecute = true }); }
        catch { }
    }

    // ================================================================= exit
    void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveAll();
        _timer.Stop();
        _cm.ShutdownBlocking();
    }
}
