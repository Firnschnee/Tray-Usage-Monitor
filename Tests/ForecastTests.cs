using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class ForecastTests
{
    private static readonly DateTime Now = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Samples every 6 minutes over the last hour, rising at ratePerHour from startPct.</summary>
    private static List<UsageSample> Rising(double startPct, double ratePerHour)
    {
        var list = new List<UsageSample>();
        for (int i = 0; i <= 10; i++)
        {
            var t = Now.AddMinutes(-60 + i * 6);
            var pct = startPct + ratePerHour * (i * 6 / 60.0);
            list.Add(new UsageSample(t, pct, 0, -1));
        }
        return list;
    }

    [Fact]
    public void SteadyBurnPredictsLimitTime()
    {
        // 50% one hour ago, +10%/h → 60% now → 100% in 4h
        var f = UsageHistory.Forecast(Rising(50, 10), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.InRange(f!.PercentPerHour, 9.5, 10.5);
        Assert.NotNull(f.LimitAtUtc);
        Assert.InRange((f.LimitAtUtc!.Value - Now).TotalHours, 3.8, 4.2);
        Assert.True(f.ReachesBeforeReset); // limit in ~4h, reset in 5h
    }

    [Fact]
    public void LimitAfterResetIsNotFlagged()
    {
        var f = UsageHistory.Forecast(Rising(50, 10), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(2), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.False(f!.ReachesBeforeReset); // limit in ~4h, reset already in 2h
        Assert.Equal("ok until reset", f.ToDisplayText());
    }

    [Fact]
    public void FlatUsageHasNoLimitTime()
    {
        var f = UsageHistory.Forecast(Rising(60, 0), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.Null(f!.LimitAtUtc);
        Assert.False(f.ReachesBeforeReset);
    }

    [Fact]
    public void TooFewSamplesReturnsNull()
    {
        var samples = Rising(50, 10).TakeLast(2).ToList();
        var f = UsageHistory.Forecast(samples, s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.Null(f);
    }

    [Fact]
    public void SamplesBeforeWindowStartAreIgnored()
    {
        // Pre-reset samples at 95% would poison the slope; window started 30 min ago
        var samples = new List<UsageSample>
        {
            new(Now.AddMinutes(-50), 95, 0, -1),
            new(Now.AddMinutes(-45), 96, 0, -1),
        };
        for (int i = 0; i <= 5; i++)
            samples.Add(new(Now.AddMinutes(-30 + i * 6), 2 + i, 0, -1));

        var f = UsageHistory.Forecast(samples, s => s.SessionPercent,
            Now.AddMinutes(-30), Now.AddHours(4), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.InRange(f!.PercentPerHour, 8, 12); // 1% per 6 min = 10%/h, not distorted by the 95s
    }
}
