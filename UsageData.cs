namespace ClaudeUsageMonitor;

/// <summary>
/// Data model for GET https://api.anthropic.com/api/oauth/usage
/// 
/// Response:
/// {
///   "five_hour":  { "utilization": 42.5, "resets_at": "2025-02-17T18:00:00Z" },
///   "seven_day":  { "utilization": 13.0, "resets_at": "2025-02-19T07:00:00Z" },
///   "extra_usage": { "is_enabled": true, "monthly_limit": 5000, "used_credits": 1250, "utilization": 25.0 }
/// }
/// </summary>
public sealed class UsageData
{
    public double SessionPercent { get; set; }
    public DateTime? SessionResetsAt { get; set; }

    public double WeeklyPercent { get; set; }
    public DateTime? WeeklyResetsAt { get; set; }
    public bool HasWeekly { get; set; }

    // Opus weekly window (seven_day_opus) – present on Max/Pro plans with an Opus cap
    public bool HasOpus { get; set; }
    public double OpusPercent { get; set; }
    public DateTime? OpusResetsAt { get; set; }

    // Extra Usage (Pay-as-you-go Overage)
    public bool ExtraEnabled { get; set; }
    public double ExtraPercent { get; set; }
    public decimal ExtraUsedDollars { get; set; }
    public decimal ExtraLimitDollars { get; set; }

    public DateTime FetchedAt { get; set; } = DateTime.Now;

    // --- Computed Properties ---

    public TimeSpan SessionResetIn => TimeUntil(SessionResetsAt);
    public TimeSpan WeeklyResetIn => TimeUntil(WeeklyResetsAt);

    public string SessionResetText => FormatSpan(SessionResetIn);
    public string WeeklyResetText => FormatSpan(WeeklyResetIn);

    public TimeSpan OpusResetIn => TimeUntil(OpusResetsAt);
    public string OpusResetText => FormatSpan(OpusResetIn);

    // Window start times (UTC) – used by the forecast to ignore pre-reset samples
    public DateTime? SessionWindowStartUtc => SessionResetsAt?.Subtract(SessionWindow);
    public DateTime? WeeklyWindowStartUtc => WeeklyResetsAt?.Subtract(WeeklyWindow);

    // Pacing: linear expected usage based on elapsed time in the window
    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    public double SessionExpectedPercent => ExpectedPercent(SessionResetsAt, SessionWindow);
    public double WeeklyExpectedPercent => ExpectedPercent(WeeklyResetsAt, WeeklyWindow);

    public double SessionPaceDiff => SessionPercent - SessionExpectedPercent;
    public double WeeklyPaceDiff => WeeklyPercent - WeeklyExpectedPercent;

    public string SessionPaceText => FormatPace(SessionPaceDiff);
    public string WeeklyPaceText => FormatPace(WeeklyPaceDiff);

    public string TooltipText
    {
        get
        {
            var s = $"Session: {SessionPercent:0}% | Reset: {SessionResetText}";
            if (HasWeekly)
            {
                s += $"\nWeekly: {WeeklyPercent:0}% ({WeeklyPaceText})";
            }
            if (ExtraEnabled) s += $"\nExtra: ${ExtraUsedDollars:F2}/${ExtraLimitDollars:F2}";
            s += $"\nUpdated: {FetchedAt:HH:mm:ss}";
            return s.Length > 127 ? s[..127] : s;
        }
    }

    private static TimeSpan TimeUntil(DateTime? utc)
    {
        if (!utc.HasValue) return TimeSpan.Zero;
        var d = utc.Value.ToLocalTime() - DateTime.Now;
        return d > TimeSpan.Zero ? d : TimeSpan.Zero;
    }

    private static string FormatSpan(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "--:--";
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static double ExpectedPercent(DateTime? resetsAtUtc, TimeSpan window)
    {
        if (!resetsAtUtc.HasValue) return 0;
        var windowStart = resetsAtUtc.Value.ToLocalTime() - window;
        var elapsed = DateTime.Now - windowStart;
        if (elapsed <= TimeSpan.Zero) return 0;
        var pct = elapsed.TotalSeconds / window.TotalSeconds * 100;
        return Math.Clamp(pct, 0, 100);
    }

    private static string FormatPace(double diff)
    {
        if (diff >= 5) return $"+{diff:0}% ahead";
        if (diff <= -5) return $"{diff:0}% under";
        return "on pace";
    }
}
