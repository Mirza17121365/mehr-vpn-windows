using System.Diagnostics;
using System.Text;

namespace MehrVpn.Services;

public class SingBoxController
{
    Process _proc;
    volatile bool _stopping;

    public event Action Exited;
    public bool IsRunning => _proc != null && !_proc.HasExited;

    public static string FindExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "deps", "singbox", "sing-box.exe"),
            Path.Combine(baseDir, "sing-box.exe")
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Runs "sing-box check" and throws with the output if the config is invalid.</summary>
    public async Task CheckConfigAsync(string exe, string configPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "check -c \"" + configPath + "\"",
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var p = Process.Start(psi);
        string output = await p.StandardOutput.ReadToEndAsync();
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string all = (output + "\n" + err).Trim();
        if (p.ExitCode != 0)
        {
            Logger.Write("singbox", "config check failed: " + all);
            throw new Exception("کانفیگ sing-box معتبر نیست: " + FirstLine(all));
        }
    }

    static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s.Substring(0, i).Trim();
    }

    public async Task StartAsync(string exe, string configPath, int timeoutSec = 25)
    {
        if (IsRunning) throw new InvalidOperationException("sing-box already running");
        _stopping = false;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "run -c \"" + configPath + "\" -D \"" + SettingsStore.DataDir + "\"",
            WorkingDirectory = Path.GetDirectoryName(exe),   // wintun.dll is next to sing-box.exe
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string lastError = null;

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        DataReceivedEventHandler onLine = (s, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            Logger.Write("singbox", e.Data);
            if (e.Data.Contains("sing-box started"))
                ready.TrySetResult(true);
            if (e.Data.Contains("FATAL") || e.Data.Contains("ERROR"))
                lastError = e.Data;
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
        Logger.Write("app", "sing-box started, pid " + _proc.Id);
        PidFile.Add("singbox", _proc.Id);

        var done = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
        if (done != ready.Task)
        {
            await StopAsync();
            throw new TimeoutException("sing-box بالا نیامد");
        }
        if (!ready.Task.Result)
        {
            await StopAsync();
            throw new Exception("sing-box بسته شد: " + (lastError ?? "لاگ را ببینید"));
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
                try { p.Kill(true); } catch { }
                await Task.Run(() => p.WaitForExit(5000));
            }
        }
        catch { }
        finally
        {
            PidFile.Remove("singbox");
            _proc = null;
            Logger.Write("app", "sing-box stopped");
        }
    }
}
