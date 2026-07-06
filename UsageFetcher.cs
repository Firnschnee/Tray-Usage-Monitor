using System.Net;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// Fetches usage data from the Anthropic OAuth API.
///
/// A single HTTP call:
///   GET https://api.anthropic.com/api/oauth/usage
///   Authorization: Bearer {accessToken}
///   anthropic-beta: oauth-2025-04-20
///   User-Agent: claude-code/{installed version, read dynamically via ClaudeCodeInfo}
/// </summary>
public sealed class UsageFetcher : IDisposable
{
    private readonly HttpClient _http;

    public UsageFetcher()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>
    /// Fetches usage data with the given OAuth Access Token.
    /// </summary>
    public async Task<UsageData> FetchAsync(string accessToken, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/oauth/usage");
        req.Headers.Add("Authorization", $"Bearer {accessToken}");
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.Add("User-Agent", ClaudeCodeInfo.UserAgent);

        var resp = await _http.SendAsync(req, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("OAuth token expired or invalid. Please run 'claude login'.");

        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[Usage] Response: {json}");
#endif

        return Parse(json);
    }

    internal static UsageData Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = new UsageData { FetchedAt = DateTime.Now };

        // five_hour
        if (root.TryGetProperty("five_hour", out var fh))
        {
            if (fh.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number) data.SessionPercent = u.GetDouble();
            if (fh.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
                if (DateTime.TryParse(r.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    data.SessionResetsAt = dt.ToUniversalTime();
        }

        // seven_day
        if (root.TryGetProperty("seven_day", out var sd))
        {
            data.HasWeekly = true;
            if (sd.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number) data.WeeklyPercent = u.GetDouble();
            if (sd.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
                if (DateTime.TryParse(r.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    data.WeeklyResetsAt = dt.ToUniversalTime();
        }

        // seven_day_opus (Opus weekly cap, same shape as seven_day)
        if (root.TryGetProperty("seven_day_opus", out var sdo))
        {
            data.HasOpus = true;
            if (sdo.TryGetProperty("utilization", out var uo) && uo.ValueKind == JsonValueKind.Number) data.OpusPercent = uo.GetDouble();
            if (sdo.TryGetProperty("resets_at", out var ro) && ro.ValueKind == JsonValueKind.String)
                if (DateTime.TryParse(ro.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    data.OpusResetsAt = dt.ToUniversalTime();
        }

        // extra_usage (Pay-as-you-go)
        if (root.TryGetProperty("extra_usage", out var ex))
        {
            if (ex.TryGetProperty("is_enabled", out var en) && en.ValueKind != JsonValueKind.Null) data.ExtraEnabled = en.GetBoolean();
            if (ex.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number) data.ExtraPercent = u.GetDouble();
            // API returns cents, convert to dollars
            if (ex.TryGetProperty("used_credits", out var uc) && uc.ValueKind == JsonValueKind.Number) data.ExtraUsedDollars = uc.GetDecimal() / 100m;
            if (ex.TryGetProperty("monthly_limit", out var ml) && ml.ValueKind == JsonValueKind.Number) data.ExtraLimitDollars = ml.GetDecimal() / 100m;
        }

        return data;
    }

    public void Dispose() => _http.Dispose();
}
