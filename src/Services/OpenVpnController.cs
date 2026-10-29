using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MehrVpn.Services;

public class OpenVpnController
{
    Process _proc;
    TcpClient _mgmt;
    string _authFile;
    volatile bool _stopping;

    public event Action<long, long> Bytes;     // total in, total out
    public event Action Exited;                // process ended unexpectedly

    public bool IsRunning => _proc != null && !_proc.HasExited;

    public static string FindExe(string custom)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(custom)) candidates.Add(custom);
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenVPN", "bin", "openvpn.exe"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "OpenVPN", "bin", "openvpn.exe"));
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Reads "remote host port" lines from an .ovpn file.</summary>
    public static List<string> ReadRemoteHosts(string profilePath)
    {
        var hosts = new List<string>();
        foreach (var raw in File.ReadAllLines(profilePath))
        {
            var line = raw.Trim();
            if (!line.StartsWith("remote ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && !hosts.Contains(parts[1])) hosts.Add(parts[1]);
        }
        return hosts;
    }

    public static bool ProfileNeedsCredentials(string profilePath)
    {
        foreach (var raw in File.ReadAllLines(profilePath))
        {
            var line = raw.Trim();
            if (line.Equals("auth-user-pass", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public async Task StartAsync(string exe, string profilePath, string user, string pass, int timeoutSec = 60)
    {
        if (IsRunning) throw new InvalidOperationException("OpenVPN already running");
        _stopping = false;

        int mgmtPort = FreePort();
        var args = new StringBuilder();
        args.Append("--config \"").Append(profilePath).Append("\" ");
        args.Append("--management 127.0.0.1 ").Append(mgmtPort).Append(' ');
                args.Append("--verb 4 --mute 0 --disable-dco ");

        _authFile = null;
        if (!string.IsNullOrEmpty(user))
        {
            _authFile = Path.Combine(SettingsStore.DataDir, "auth.tmp");
            File.WriteAllText(_authFile, user + "\n" + (pass ?? "") + "\n");
            args.Append("--auth-user-pass \"").Append(_authFile).Append("\" ");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args.ToString(),
            WorkingDirectory = Path.GetDirectoryName(profilePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string failReason = null;

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        DataReceivedEventHandler onLine = (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Logger.Write("openvpn", e.Data);
            if (e.Data.Contains("Initialization Sequence Completed"))
                ready.TrySetResult(true);
            else if (e.Data.Contains("AUTH_FAILED"))
            {
                failReason = "نام کاربری یا رمز OpenVPN اشتباه است";
            }
            else if (e.Data.Contains("Exiting due to fatal error"))
            {
                failReason ??= "OpenVPN با خطا بسته شد (لاگ را ببینید)";
            }
        };
        _proc.OutputDataReceived += onLine;
        _proc.ErrorDataReceived += onLine;
        _proc.Exited += (s, e) =>
        {
            ready.TrySetResult(false);
            if (!_stopping) Exited?.Invoke();
        };

        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        Logger.Write("app", "openvpn started, pid " + _proc.Id);
        PidFile.Add("openvpn", _proc.Id);

        _ = Task.Run(() => ConnectManagementAsync(mgmtPort));

        var done = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
        DeleteAuthFile();
        if (done != ready.Task)
        {
            await StopAsync();
            throw new TimeoutException("اتصال OpenVPN بعد از " + timeoutSec + " ثانیه برقرار نشد");
        }
        if (!ready.Task.Result)
        {
            await StopAsync();
            throw new Exception(failReason ?? "OpenVPN قبل از اتصال بسته شد");
        }
    }

    void DeleteAuthFile()
    {
        try { if (_authFile != null && File.Exists(_authFile)) File.Delete(_authFile); } catch { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    async Task ConnectManagementAsync(int port)
    {
        for (int i = 0; i < 40 && !_stopping; i++)
        {
            try
            {
                _mgmt = new TcpClient();
                await _mgmt.ConnectAsync(IPAddress.Loopback, port);
                break;
            }
            catch
            {
                _mgmt?.Dispose();
                _mgmt = null;
                await Task.Delay(250);
            }
        }
        if (_mgmt == null) return;

        try
        {
            var stream = _mgmt.GetStream();
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
            var reader = new StreamReader(stream, Encoding.UTF8);
            await writer.WriteLineAsync("bytecount 2");
            var rx = new Regex(@">BYTECOUNT:(\d+),(\d+)");
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                var m = rx.Match(line);
                if (m.Success)
                    Bytes?.Invoke(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
            }
        }
        catch
        {
            // connection closed on shutdown
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        var p = _proc;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                try
                {
                    if (_mgmt != null && _mgmt.Connected)
                    {
                        var w = new StreamWriter(_mgmt.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                        await w.WriteLineAsync("signal SIGTERM");
                    }
                }
                catch { }

                var exited = await Task.Run(() => p.WaitForExit(6000));
                if (!exited)
                {
                    try { p.Kill(true); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            try { _mgmt?.Dispose(); } catch { }
            _mgmt = null;
            PidFile.Remove("openvpn");
            DeleteAuthFile();
            _proc = null;
            Logger.Write("app", "openvpn stopped");
        }
    }
}
