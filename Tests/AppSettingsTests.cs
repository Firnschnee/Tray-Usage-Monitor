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

    [Fact]
    public void DefaultWidgetOffsetIsZero()
    {
        var s = AppSettings.Load();
        Assert.Equal(0, s.WidgetOffsetX);
    }

    [Fact]
    public void WidgetOffsetRoundTrips()
    {
        var s = new AppSettings { WidgetOffsetX = 240 };
        s.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(240, loaded.WidgetOffsetX);
    }

    [Fact]
    public void NegativeWidgetOffsetIsClampedToZero()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"WidgetOffsetX":-50}""");
        var s = AppSettings.Load();
        Assert.Equal(0, s.WidgetOffsetX);
    }

    // ── TaskbarWidget.ClampWidgetX ──────────────────────────────────────────

    [Fact]
    public void ClampWidgetXKeepsInRangeValueUntouched()
        => Assert.Equal(120, TaskbarWidget.ClampWidgetX(120, 500));

    [Fact]
    public void ClampWidgetXPinsToTrayWhenPastRightEdge()
        => Assert.Equal(500, TaskbarWidget.ClampWidgetX(9000, 500));

    [Fact]
    public void ClampWidgetXPinsToLeftEdgeWhenNegative()
        => Assert.Equal(0, TaskbarWidget.ClampWidgetX(-40, 500));

    [Fact]
    public void ClampWidgetXHandlesTinyTaskbar()
        => Assert.Equal(0, TaskbarWidget.ClampWidgetX(100, -20));

    // ── Accent color ────────────────────────────────────────────────────────

    [Fact]
    public void DefaultAccentIsGreen()
        => Assert.Equal(AccentColor.Green, AppSettings.Load().Accent);

    [Fact]
    public void AccentRoundTrips()
    {
        new AppSettings { Accent = AccentColor.Amber }.Save();
        Assert.Equal(AccentColor.Amber, AppSettings.Load().Accent);
    }

    [Fact]
    public void InvalidAccentFallsBackToGreen()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """{"Accent":42}""");
        Assert.Equal(AccentColor.Green, AppSettings.Load().Accent);
    }

    [Fact]
    public void AccentPaletteMapsAmber()
        => Assert.Equal(System.Drawing.Color.FromArgb(250, 189, 47), AccentPalette.Ok(AccentColor.Amber));
}
