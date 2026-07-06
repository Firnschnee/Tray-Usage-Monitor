using Xunit;

namespace ClaudeUsageMonitor.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cum-tests-" + Guid.NewGuid().ToString("N"));
        AppSettings.SettingsDir = _dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void LoadWithoutFileReturnsDefaults()
    {
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);
        Assert.True(s.NotificationsEnabled);
        Assert.Equal(75, s.WarnThresholdPercent);
        Assert.Equal(90, s.CritThresholdPercent);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var s = new AppSettings { PollIntervalMinutes = 5, NotificationsEnabled = false, WarnThresholdPercent = 60, CritThresholdPercent = 80 };
        s.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(5, loaded.PollIntervalMinutes);
        Assert.False(loaded.NotificationsEnabled);
        Assert.Equal(60, loaded.WarnThresholdPercent);
        Assert.Equal(80, loaded.CritThresholdPercent);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{not json");
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);
    }

    [Fact]
    public void InvalidValuesAreSanitized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"PollIntervalMinutes":7,"WarnThresholdPercent":95,"CritThresholdPercent":10}""");
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);          // 7 is not an allowed interval
        Assert.Equal(95, s.WarnThresholdPercent);
        Assert.True(s.CritThresholdPercent >= s.WarnThresholdPercent); // crit clamped up to warn
    }
}
