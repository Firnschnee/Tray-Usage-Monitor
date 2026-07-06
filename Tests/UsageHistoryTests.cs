using Xunit;

namespace ClaudeUsageMonitor.Tests;

public sealed class UsageHistoryTests : IDisposable
{
    private readonly string _file;

    public UsageHistoryTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "cum-hist-" + Guid.NewGuid().ToString("N"), "history.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_file)!, true); } catch { }
    }

    [Fact]
    public void AddPersistsAndReloads()
    {
        var h = new UsageHistory(_file);
        h.Add(new UsageSample(new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc), 10, 20, -1));

        var reloaded = new UsageHistory(_file);
        Assert.Single(reloaded.Samples);
        Assert.Equal(10, reloaded.Samples[0].SessionPercent);
        Assert.Equal(-1, reloaded.Samples[0].OpusPercent);
    }

    [Fact]
    public void PrunesSamplesOlderThan14Days()
    {
        var now = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
        var h = new UsageHistory(_file);
        h.Add(new UsageSample(now.AddDays(-15), 1, 1, -1));
        h.Add(new UsageSample(now.AddDays(-13), 2, 2, -1));
        h.Add(new UsageSample(now, 3, 3, -1));
        Assert.Equal(2, h.Samples.Count);
        Assert.DoesNotContain(h.Samples, s => s.SessionPercent == 1);
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "not json at all");
        var h = new UsageHistory(_file);
        Assert.Empty(h.Samples);
        h.Add(new UsageSample(DateTime.UtcNow, 5, 5, -1)); // must not throw
        Assert.Single(h.Samples);
    }
}
