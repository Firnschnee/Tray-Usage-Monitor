using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>Aggregated token usage for one model. Cost is the API-equivalent price in USD.</summary>
public sealed record ModelUsage(string Model, long InputTokens, long OutputTokens, long CacheCreateTokens, long CacheReadTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreateTokens + CacheReadTokens;

    public decimal CostUsd
    {
        get
        {
            var (pin, pout, pwrite, pread) = Prices(Model);
            return (InputTokens * pin + OutputTokens * pout
                  + CacheCreateTokens * pwrite + CacheReadTokens * pread) / 1_000_000m;
        }
    }

    // Prices per million tokens: (input, output, cache write, cache read)
    private static (decimal In, decimal Out, decimal Write, decimal Read) Prices(string model) =>
        model.Contains("haiku")  ? (1m, 5m, 1.25m, 0.10m) :
        model.Contains("sonnet") ? (3m, 15m, 3.75m, 0.30m) :
        (15m, 75m, 18.75m, 1.50m); // opus tier; also the fallback for unknown flagship models
}

public sealed record LocalUsageReport(IReadOnlyList<ModelUsage> Today, IReadOnlyList<ModelUsage> Week)
{
    public static readonly LocalUsageReport Empty = new(Array.Empty<ModelUsage>(), Array.Empty<ModelUsage>());
}

internal sealed record JsonlEntry(string MessageId, string Model, DateTime TimestampUtc,
    long InputTokens, long OutputTokens, long CacheCreateTokens, long CacheReadTokens);

/// <summary>
/// Aggregates Claude Code token usage from ~/.claude/projects/**/*.jsonl.
/// Covers ONLY Claude Code; claude.ai web usage never appears in these logs.
/// Duplicate streaming records are deduplicated by message.id.
/// </summary>
public static class JsonlUsageReader
{
    public static string DefaultProjectsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static LocalUsageReport Read(string projectsDir, DateTime todayStartUtc, DateTime weekStartUtc)
    {
        if (!Directory.Exists(projectsDir)) return LocalUsageReport.Empty;

        var seen = new HashSet<string>();
        var today = new Dictionary<string, ModelUsage>();
        var week = new Dictionary<string, ModelUsage>();

        foreach (var file in Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                // A file untouched since the week began cannot contain in-week entries
                if (File.GetLastWriteTimeUtc(file) < weekStartUtc) continue;

                // Claude Code appends to these files while running - share everything
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.Contains("\"usage\"", StringComparison.Ordinal)) continue; // cheap pre-filter
                    if (!TryParseLine(line, out var e) || e == null) continue;
                    if (e.TimestampUtc < weekStartUtc) continue;
                    if (e.MessageId.Length > 0 && !seen.Add(e.MessageId)) continue;

                    Accumulate(week, e);
                    if (e.TimestampUtc >= todayStartUtc) Accumulate(today, e);
                }
            }
            catch { /* unreadable file: skip; the sweep must never throw */ }
        }

        return new LocalUsageReport(Sorted(today), Sorted(week));
    }

    private static void Accumulate(Dictionary<string, ModelUsage> agg, JsonlEntry e)
    {
        agg[e.Model] = agg.TryGetValue(e.Model, out var m)
            ? m with
            {
                InputTokens = m.InputTokens + e.InputTokens,
                OutputTokens = m.OutputTokens + e.OutputTokens,
                CacheCreateTokens = m.CacheCreateTokens + e.CacheCreateTokens,
                CacheReadTokens = m.CacheReadTokens + e.CacheReadTokens,
            }
            : new ModelUsage(e.Model, e.InputTokens, e.OutputTokens, e.CacheCreateTokens, e.CacheReadTokens);
    }

    private static IReadOnlyList<ModelUsage> Sorted(Dictionary<string, ModelUsage> agg) =>
        agg.Values.OrderByDescending(m => m.CostUsd).ToList();

    internal static bool TryParseLine(string line, out JsonlEntry? entry)
    {
        entry = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") return false;
            if (!root.TryGetProperty("message", out var msg)) return false;
            if (!msg.TryGetProperty("usage", out var usage)) return false;

            var model = msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "";
            if (model.Length == 0 || model == "<synthetic>") return false;

            if (!root.TryGetProperty("timestamp", out var ts) ||
                !DateTime.TryParse(ts.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                return false;

            long L(string name) =>
                usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

            var id = msg.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

            entry = new JsonlEntry(id, model, when.ToUniversalTime(),
                L("input_tokens"), L("output_tokens"),
                L("cache_creation_input_tokens"), L("cache_read_input_tokens"));
            return true;
        }
        catch (JsonException) { return false; }
    }
}
