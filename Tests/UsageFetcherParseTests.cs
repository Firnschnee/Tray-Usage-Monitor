using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class UsageFetcherParseTests
{
    private const string FullJson = """
        {
          "five_hour":  { "utilization": 42.5, "resets_at": "2026-07-06T18:00:00Z" },
          "seven_day":  { "utilization": 13.0, "resets_at": "2026-07-09T07:00:00Z" },
          "seven_day_opus": { "utilization": 5.5, "resets_at": "2026-07-09T07:00:00Z" },
          "extra_usage": { "is_enabled": false, "monthly_limit": 0, "used_credits": 0, "utilization": 0 }
        }
        """;

    [Fact]
    public void ParsesExistingWindows()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.Equal(42.5, d.SessionPercent);
        Assert.True(d.HasWeekly);
        Assert.Equal(13.0, d.WeeklyPercent);
        Assert.Equal(new DateTime(2026, 7, 6, 18, 0, 0, DateTimeKind.Utc), d.SessionResetsAt);
    }

    [Fact]
    public void ParsesOpusWindow()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.True(d.HasOpus);
        Assert.Equal(5.5, d.OpusPercent);
        Assert.Equal(new DateTime(2026, 7, 9, 7, 0, 0, DateTimeKind.Utc), d.OpusResetsAt);
    }

    [Fact]
    public void MissingOpusMeansHasOpusFalse()
    {
        var d = UsageFetcher.Parse("""{ "five_hour": { "utilization": 10, "resets_at": "2026-07-06T18:00:00Z" } }""");
        Assert.False(d.HasOpus);
        Assert.False(d.HasWeekly);
    }

    [Fact]
    public void WindowStartsAreResetMinusWindowLength()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.Equal(new DateTime(2026, 7, 6, 13, 0, 0, DateTimeKind.Utc), d.SessionWindowStartUtc);
        Assert.Equal(new DateTime(2026, 7, 2, 7, 0, 0, DateTimeKind.Utc), d.WeeklyWindowStartUtc);
    }

    [Fact]
    public void NullWindowsAreTreatedAsAbsent()
    {
        // Real API shape: present-but-null windows must not throw and must not set Has* flags
        var d = UsageFetcher.Parse("""
            {
              "five_hour":  { "utilization": 59.0, "resets_at": "2026-07-06T16:19:59.780229+00:00" },
              "seven_day":  { "utilization": 30.0, "resets_at": "2026-07-06T21:59:59.780255+00:00" },
              "seven_day_opus": null,
              "seven_day_sonnet": null,
              "extra_usage": { "is_enabled": true, "monthly_limit": 10000, "used_credits": 0.0, "utilization": null }
            }
            """);
        Assert.False(d.HasOpus);
        Assert.True(d.HasWeekly);
        Assert.Equal(59.0, d.SessionPercent);
        Assert.True(d.ExtraEnabled);
    }
}
