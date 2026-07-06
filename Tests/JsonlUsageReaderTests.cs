using Xunit;

namespace ClaudeUsageMonitor.Tests;

public sealed class JsonlUsageReaderTests : IDisposable
{
    private readonly string _dir;

    public JsonlUsageReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cum-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "proj-a"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Line(string id, string model, string timestamp,
        long input = 100, long output = 50, long cacheCreate = 10, long cacheRead = 5) =>
        $$$$"""{"type":"assistant","timestamp":"{{{{timestamp}}}}","message":{"id":"{{{{id}}}}","model":"{{{{model}}}}","usage":{"input_tokens":{{{{input}}}},"output_tokens":{{{{output}}}},"cache_creation_input_tokens":{{{{cacheCreate}}}},"cache_read_input_tokens":{{{{cacheRead}}}}}}}""";

    [Fact]
    public void ParsesRealShapedLine()
    {
        Assert.True(JsonlUsageReader.TryParseLine(
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"), out var e));
        Assert.NotNull(e);
        Assert.Equal("msg_1", e!.MessageId);
        Assert.Equal("claude-sonnet-5", e.Model);
        Assert.Equal(100, e.InputTokens);
        Assert.Equal(5, e.CacheReadTokens);
        Assert.Equal(DateTimeKind.Utc, e.TimestampUtc.Kind);
    }

    [Fact]
    public void RejectsNonAssistantAndSynthetic()
    {
        Assert.False(JsonlUsageReader.TryParseLine("""{"type":"user","message":{"usage":{}}}""", out _));
        Assert.False(JsonlUsageReader.TryParseLine(
            Line("msg_x", "<synthetic>", "2026-07-06T10:00:00.000Z"), out _));
        Assert.False(JsonlUsageReader.TryParseLine("not json", out _));
        Assert.False(JsonlUsageReader.TryParseLine("42", out _));
        Assert.False(JsonlUsageReader.TryParseLine("[1,2,3]", out _));
        Assert.False(JsonlUsageReader.TryParseLine("null", out _));
    }

    [Fact]
    public void AggregatesDedupsAndSplitsTodayVsWeek()
    {
        var file = Path.Combine(_dir, "proj-a", "session1.jsonl");
        File.WriteAllLines(file, new[]
        {
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"),          // today
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"),          // duplicate: ignored
            Line("msg_2", "claude-fable-5",  "2026-07-04T10:00:00.000Z"),          // this week, not today
            Line("msg_3", "claude-sonnet-5", "2026-06-20T10:00:00.000Z"),          // before week: ignored
            """{"type":"summary","summary":"noise line without usage"}""",
        });
        // Entries older than the week must be filtered even though the file is new
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var report = JsonlUsageReader.Read(_dir,
            todayStartUtc: new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            weekStartUtc: new DateTime(2026, 7, 2, 7, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, report.Week.Count);   // sonnet + fable
        Assert.Single(report.Today);          // only msg_1
        var sonnetWeek = report.Week.Single(m => m.Model == "claude-sonnet-5");
        Assert.Equal(100, sonnetWeek.InputTokens); // dedup: counted once
    }

    [Fact]
    public void MissingDirectoryReturnsEmpty()
    {
        var report = JsonlUsageReader.Read(Path.Combine(_dir, "does-not-exist"), DateTime.UtcNow, DateTime.UtcNow);
        Assert.Empty(report.Week);
        Assert.Empty(report.Today);
    }

    [Fact]
    public void CostUsesPerModelPricing()
    {
        // Sonnet: $3/M in + $15/M out - 1M each = $18
        var sonnet = new ModelUsage("claude-sonnet-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(18m, sonnet.CostUsd);

        // Haiku: $1/M in + $5/M out
        var haiku = new ModelUsage("claude-haiku-4-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(6m, haiku.CostUsd);

        // Unknown flagship falls back to opus-tier pricing: $15/M in + $75/M out
        var fable = new ModelUsage("claude-fable-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(90m, fable.CostUsd);
    }
}
