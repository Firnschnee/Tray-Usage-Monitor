using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>One poll snapshot. Percent value -1 means the window was not reported.</summary>
public sealed record UsageSample(DateTime TimestampUtc, double SessionPercent, double WeeklyPercent, double OpusPercent);

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
}
