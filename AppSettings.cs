using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeUsageMonitor;

/// <summary>
/// User settings, persisted as JSON in %APPDATA%\ClaudeUsageMonitor\settings.json.
/// Autostart is not part of the JSON; it lives in the HKCU Run key.
/// </summary>
public sealed class AppSettings
{
    public int PollIntervalMinutes { get; set; } = 2;
    public bool NotificationsEnabled { get; set; } = true;
    public int WarnThresholdPercent { get; set; } = 75;
    public int CritThresholdPercent { get; set; } = 90;

    // Settable so tests can redirect persistence to a temp dir.
    public static string SettingsDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageMonitor");

    private static string FilePath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null) return Sanitize(s);
            }
        }
        catch { /* corrupt file: fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort; in-memory settings stay valid */ }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        if (s.PollIntervalMinutes is not (1 or 2 or 5 or 15)) s.PollIntervalMinutes = 2;
        s.WarnThresholdPercent = Math.Clamp(s.WarnThresholdPercent, 1, 99);
        s.CritThresholdPercent = Math.Clamp(s.CritThresholdPercent, s.WarnThresholdPercent, 100);
        return s;
    }

    // ── Autostart (HKCU Run key) ────────────────────────────────────────────

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClaudeUsageMonitor";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    public static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled) key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue(RunValueName, false);
        }
        catch { /* registry write failed, autostart state unchanged */ }
    }
}
