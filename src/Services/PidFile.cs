using System.Diagnostics;
using System.Text.Json;

namespace MehrVpn.Services;

/// <summary>Remembers child process ids so leftovers from a crash can be killed on next start.</summary>
public static class PidFile
{
    static readonly object Gate = new();
    static string PathFile => Path.Combine(SettingsStore.DataDir, "pids.json");

    static Dictionary<string, int> Read()
    {
        try
        {
            if (File.Exists(PathFile))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(PathFile))
                       ?? new Dictionary<string, int>();
        }
        catch { }
        return new Dictionary<string, int>();
    }

    static void Write(Dictionary<string, int> d)
    {
        try { File.WriteAllText(PathFile, JsonSerializer.Serialize(d)); } catch { }
    }

    public static void Add(string name, int pid)
    {
        lock (Gate)
        {
            var d = Read();
            d[name] = pid;
            Write(d);
        }
    }

    public static void Remove(string name)
    {
        lock (Gate)
        {
            var d = Read();
            d.Remove(name);
            Write(d);
        }
    }

    public static void KillLeftovers()
    {
        lock (Gate)
        {
            var d = Read();
            foreach (var kv in d)
            {
                try
                {
                    var p = Process.GetProcessById(kv.Value);
                    string expected = kv.Key == "openvpn" ? "openvpn" : "sing-box";
                    if (p.ProcessName.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(true);
                        Logger.Write("app", "killed leftover " + kv.Key + " pid " + kv.Value);
                    }
                }
                catch { }
            }
            Write(new Dictionary<string, int>());
        }
    }
}
