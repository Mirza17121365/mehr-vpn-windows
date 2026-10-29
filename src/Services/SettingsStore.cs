using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MehrVpn.Services;

public static class SettingsStore
{
    public static string DataDir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MehrVpn");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string ProfilePath => Path.Combine(DataDir, "office.ovpn");
    public static string SingBoxConfigPath => Path.Combine(DataDir, "singbox-config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            Logger.Write("settings", "load failed: " + ex.Message);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Options));
        }
        catch (Exception ex)
        {
            Logger.Write("settings", "save failed: " + ex.Message);
        }
    }

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        byte[] data = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        try
        {
            byte[] data = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedB64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }
}
