using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// App-Einstellungen + sichere Credential-Speicherung.
/// Alles in %LOCALAPPDATA%\ClaudeUsageMonitor\.
/// </summary>
public sealed class AppSettings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeUsageMonitor");

    private static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
    private static readonly string SessionFile = Path.Combine(Dir, ".session");

    public int PollIntervalSeconds { get; set; } = 120;
    public bool AutoStart { get; set; }
    public int WarnAtPercent { get; set; } = 80;

    // --- Settings Load/Save ---

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile))
                       ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsFile,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // --- SessionKey Storage (DPAPI-verschlüsselt) ---

    public static void SaveSessionKey(string sessionKey)
    {
        Directory.CreateDirectory(Dir);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(sessionKey), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionFile, encrypted);
    }

    public static string? LoadSessionKey()
    {
        try
        {
            if (!File.Exists(SessionFile)) return null;
            var encrypted = File.ReadAllBytes(SessionFile);
            if (encrypted.Length == 0) return null;
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public static void DeleteSessionKey()
    {
        try { if (File.Exists(SessionFile)) File.Delete(SessionFile); } catch { }
    }

    public static bool HasSessionKey() => !string.IsNullOrWhiteSpace(LoadSessionKey());

    // --- Autostart ---

    public void ApplyAutoStart()
    {
        const string keyName = "ClaudeUsageMonitor";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (AutoStart)
                key.SetValue(keyName, $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"");
            else
                key.DeleteValue(keyName, throwOnMissingValue: false);
        }
        catch { }
    }
}
