namespace ClaudeUsageMonitor;

/// <summary>
/// Datenmodell für die claude.ai Usage-API Response.
/// 
/// Die API GET /api/organizations/{orgId}/usage liefert JSON:
/// {
///   "five_hour": { "utilization": 42.5, "resets_at": "2025-02-17T18:00:00Z" },
///   "seven_day": { "utilization": 13.0, "resets_at": "2025-02-19T07:00:00Z" }
/// }
/// </summary>
public sealed class UsageData
{
    // --- Session (5-Stunden-Fenster) ---
    public double SessionPercent { get; set; }
    public DateTime? SessionResetsAt { get; set; }

    // --- Weekly (7-Tage-Fenster) ---
    public double WeeklyPercent { get; set; }
    public DateTime? WeeklyResetsAt { get; set; }
    public bool HasWeeklyLimit { get; set; }

    // --- Meta ---
    public DateTime FetchedAt { get; set; } = DateTime.Now;
    public string? RawJson { get; set; }

    // --- Berechnete Properties ---

    public TimeSpan SessionResetIn =>
        SessionResetsAt.HasValue
            ? (SessionResetsAt.Value.ToLocalTime() - DateTime.Now) is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero
            : TimeSpan.Zero;

    public TimeSpan WeeklyResetIn =>
        WeeklyResetsAt.HasValue
            ? (WeeklyResetsAt.Value.ToLocalTime() - DateTime.Now) is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero
            : TimeSpan.Zero;

    public string SessionResetFormatted => FormatTimeSpan(SessionResetIn);
    public string WeeklyResetFormatted => FormatTimeSpan(WeeklyResetIn);

    /// <summary>Tooltip-Text für NotifyIcon (max 127 Zeichen).</summary>
    public string TooltipText
    {
        get
        {
            var lines = new List<string>
            {
                "Claude Usage Monitor",
                $"Session: {SessionPercent:0}% | Reset: {SessionResetFormatted}",
            };
            if (HasWeeklyLimit)
                lines.Add($"Weekly: {WeeklyPercent:0}% | Reset: {WeeklyResetFormatted}");
            lines.Add($"Updated: {FetchedAt:HH:mm:ss}");

            var text = string.Join("\n", lines);
            return text.Length > 127 ? text[..127] : text;
        }
    }

    /// <summary>Ausführlicher Detail-Text.</summary>
    public string DetailText
    {
        get
        {
            var lines = new List<string>
            {
                "Session (5h Window)",
                $"  Usage: {SessionPercent:0.0}%",
                $"  Reset in: {SessionResetFormatted}",
                SessionResetsAt.HasValue ? $"  Reset at: {SessionResetsAt.Value.ToLocalTime():HH:mm:ss}" : "",
            };
            if (HasWeeklyLimit)
            {
                lines.Add("");
                lines.Add("Weekly (7-Day Window)");
                lines.Add($"  Usage: {WeeklyPercent:0.0}%");
                lines.Add($"  Reset in: {WeeklyResetFormatted}");
            }
            lines.Add("");
            lines.Add($"Last Update: {FetchedAt:HH:mm:ss}");
            return string.Join(Environment.NewLine, lines.Where(l => l != ""));
        }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "--:--";
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
