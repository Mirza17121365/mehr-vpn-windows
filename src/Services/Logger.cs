using System.Text;

namespace MehrVpn.Services;

public static class Logger
{
    static readonly object Gate = new();
    static readonly StringBuilder Buffer = new();
    public static event Action<string> LineAdded;

    public static string LogFile =>
        Path.Combine(SettingsStore.DataDir, "app.log");

    public static void Write(string source, string message)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + " [" + source + "] " + message;
        lock (Gate)
        {
            Buffer.AppendLine(line);
            if (Buffer.Length > 400_000)
                Buffer.Remove(0, 100_000);
            try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
        }
        LineAdded?.Invoke(line);
    }

    public static string All()
    {
        lock (Gate) return Buffer.ToString();
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        try { File.WriteAllText(LogFile, ""); } catch { }
    }
}
