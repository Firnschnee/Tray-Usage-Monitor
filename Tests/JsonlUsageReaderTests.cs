using System.Security.AccessControl;
using System.Security.Principal;
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
    public void SkipsInaccessibleSubdirectories()
    {
        var good = Path.Combine(_dir, "proj-a", "session1.jsonl");
        File.WriteAllLines(good, new[] { Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z") });
        File.SetLastWriteTimeUtc(good, DateTime.UtcNow);

        var locked = Directory.CreateDirectory(Path.Combine(_dir, "proj-locked"));
        File.WriteAllText(Path.Combine(locked.FullName, "hidden.jsonl"),
            Line("msg_2", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"));

        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var sec = locked.GetAccessControl();
        sec.AddAccessRule(rule);
        locked.SetAccessControl(sec);

        try
        {
            var report = JsonlUsageReader.Read(_dir,
                todayStartUtc: new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
                weekStartUtc: new DateTime(2026, 7, 2, 7, 0, 0, DateTimeKind.Utc));

            // The accessible project must still be aggregated; the locked one is skipped.
            var sonnet = Assert.Single(report.Week);
            Assert.Equal(100, sonnet.InputTokens);
        }
        finally
        {
            sec.RemoveAccessRule(rule);
            locked.SetAccessControl(sec);
        }
    }

    [Fact]
    public void MissingDirectoryReturnsEmpty()
    {
        var report = JsonlUsageReader.Read(Path.Combine(_dir, "does-not-exist"), DateTime.UtcNow, DateTime.UtcNow);
        Assert.Empty(report.Week);
        Assert.Empty(report.Today);
    }

    // Fixed reference date inside the Sonnet 5 intro-pricing period
    private static readonly DateTime July2026 = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CostUsesPerModelPricing()
    {
        // Haiku: $1/M in + $5/M out
        Assert.Equal(6m, new ModelUsage("claude-haiku-4-5", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));

        // Fable: $10/M in + $50/M out
        Assert.Equal(60m, new ModelUsage("claude-fable-5", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));

        // Fable cache: $12.50/M write (5m) + $1/M read
        Assert.Equal(13.50m, new ModelUsage("claude-fable-5", 0, 0, 1_000_000, 1_000_000).CostAt(July2026));

        // Modern Opus (4.5+): $5/M in + $25/M out
        Assert.Equal(30m, new ModelUsage("claude-opus-4-8", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));

        // Legacy Opus 4.1 / Opus 4: $15/M in + $75/M out
        Assert.Equal(90m, new ModelUsage("claude-opus-4-1-20250805", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));
        Assert.Equal(90m, new ModelUsage("claude-opus-4-20250514", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));

        // Older Sonnet: standard $3/M in + $15/M out
        Assert.Equal(18m, new ModelUsage("claude-sonnet-4-6", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));

        // Unknown flagship falls back to fable-tier pricing
        Assert.Equal(60m, new ModelUsage("claude-nova-6", 1_000_000, 1_000_000, 0, 0).CostAt(July2026));
    }

    [Fact]
    public void Sonnet5IntroPricingEndsSeptember2026()
    {
        var sonnet5 = new ModelUsage("claude-sonnet-5", 1_000_000, 1_000_000, 0, 0);

        // Intro: $2/M in + $10/M out through 2026-08-31
        Assert.Equal(12m, sonnet5.CostAt(July2026));
        Assert.Equal(12m, sonnet5.CostAt(new DateTime(2026, 8, 31, 23, 59, 0, DateTimeKind.Utc)));

        // Standard: $3/M in + $15/M out from 2026-09-01
        Assert.Equal(18m, sonnet5.CostAt(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
