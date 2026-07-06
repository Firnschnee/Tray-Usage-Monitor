using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>One poll snapshot. Percent value -1 means the window was not reported.</summary>
public sealed record UsageSample(DateTime TimestampUtc, double SessionPercent, double WeeklyPercent, double OpusPercent);

/// <summary>Linear burn-rate extrapolation result.</summary>
public sealed record ForecastResult(double PercentPerHour, DateTime? LimitAtUtc, bool ReachesBeforeReset)
{
    public string ToDisplayText()
    {
        if (LimitAtUtc == null || !ReachesBeforeReset) return "ok until reset";
        var local = LimitAtUtc.Value.ToLocalTime();
        return local.Date == DateTime.Now.Date ? $"limit ~{local:HH:mm}" : $"limit {local:ddd HH:mm}";
    }
}

/// <summary>
/// Persistent store of poll samples (JSON array in %APPDATA%\ClaudeUsageMonitor\history.json).
/// Keeps 14 days: enough for weekly forecasts and the data supply for future history charts.
/// </summary>
public sealed class UsageHistory
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly string _filePath;
    private readonly List<UsageSample> _samples = new();

    public IReadOnlyList<UsageSample> Samples => _samples;

    public UsageHistory(string filePath)
    {
        _filePath = filePath;
        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize<List<UsageSample>>(File.ReadAllText(filePath));
                if (loaded != null) _samples.AddRange(loaded);
            }
        }
        catch { /* corrupt history: start fresh */ }
    }

    public void Add(UsageSample sample)
    {
        _samples.Add(sample);
        var cutoff = sample.TimestampUtc - Retention;
        _samples.RemoveAll(s => s.TimestampUtc < cutoff);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_samples));
        }
        catch { /* persistence is best-effort; in-memory data stays valid */ }
    }

    /// <summary>
    /// Least-squares slope over the samples inside the current limit window,
    /// capped to a lookback horizon. Returns null when there is not enough data
    /// (fewer than 3 samples or spanning less than 10 minutes).
    /// </summary>
    public static ForecastResult? Forecast(
        IReadOnlyList<UsageSample> samples, Func<UsageSample, double> percent,
        DateTime windowStartUtc, DateTime? resetsAtUtc, DateTime nowUtc, TimeSpan lookback)
    {
        var cutoff = nowUtc - lookback;
        if (windowStartUtc > cutoff) cutoff = windowStartUtc;

        var pts = samples.Where(s => s.TimestampUtc >= cutoff && s.TimestampUtc <= nowUtc)
                         .OrderBy(s => s.TimestampUtc)
                         .ToList();
        if (pts.Count < 3) return null;
        if (pts[^1].TimestampUtc - pts[0].TimestampUtc < TimeSpan.FromMinutes(10)) return null;

        var t0 = pts[0].TimestampUtc;
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in pts)
        {
            double x = (p.TimestampUtc - t0).TotalHours, y = percent(p);
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double denom = n * sxx - sx * sx;
        if (denom <= 0) return null;
        double slope = (n * sxy - sx * sy) / denom;

        if (slope <= 0.01) return new ForecastResult(slope, null, false);

        double current = percent(pts[^1]);
        var limitAt = nowUtc.AddHours((100 - current) / slope);
        bool before = resetsAtUtc.HasValue && limitAt < resetsAtUtc.Value;
        return new ForecastResult(slope, limitAt, before);
    }
}
