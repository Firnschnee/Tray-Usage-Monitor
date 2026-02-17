using System.Net;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// HTTP-Client für die interne claude.ai API.
/// 
/// Endpoints (identisch zum Electron-Widget):
///   GET /api/organizations                          → [{uuid, name, ...}]
///   GET /api/organizations/{orgId}/usage            → {five_hour, seven_day}
/// 
/// Auth: Cookie-Header mit "sessionKey={value}" — nur der sessionKey reicht.
/// </summary>
public sealed class ClaudeApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _sessionKey;
    private string? _organizationId;

    public string? OrganizationId => _organizationId;

    public ClaudeApiClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://claude.ai"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>Setzt den sessionKey für API-Calls.</summary>
    public void SetSessionKey(string sessionKey)
    {
        _sessionKey = sessionKey;
        _organizationId = null;
    }

    /// <summary>Holt die Organization-ID (gecacht nach erstem Call).</summary>
    public async Task<string> GetOrganizationIdAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(_organizationId))
            return _organizationId;

        var response = await SendRequest("/api/organizations", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var org = root[0];
            _organizationId = org.TryGetProperty("uuid", out var u) ? u.GetString()
                            : org.TryGetProperty("id", out var i) ? i.GetString()
                            : null;
        }

        return _organizationId
            ?? throw new Exception($"Organization nicht gefunden. Response: {json[..Math.Min(300, json.Length)]}");
    }

    /// <summary>Fetcht Usage-Daten vom korrekten Endpoint.</summary>
    public async Task<UsageData> FetchUsageAsync(CancellationToken ct = default)
    {
        var orgId = await GetOrganizationIdAsync(ct);
        var response = await SendRequest($"/api/organizations/{orgId}/usage", ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        return ParseUsage(json);
    }

    /// <summary>
    /// Parst die /usage Response.
    /// Format: { "five_hour": { "utilization": 42.5, "resets_at": "..." }, "seven_day": { ... } }
    /// </summary>
    private static UsageData ParseUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = new UsageData { FetchedAt = DateTime.Now, RawJson = json };

        // --- five_hour (Session) ---
        if (root.TryGetProperty("five_hour", out var fiveHour))
        {
            if (fiveHour.TryGetProperty("utilization", out var util))
                data.SessionPercent = util.GetDouble();

            if (fiveHour.TryGetProperty("resets_at", out var resets) &&
                resets.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(resets.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                data.SessionResetsAt = dt.ToUniversalTime();
            }
        }

        // --- seven_day (Weekly) ---
        if (root.TryGetProperty("seven_day", out var sevenDay))
        {
            data.HasWeeklyLimit = true;

            if (sevenDay.TryGetProperty("utilization", out var util))
                data.WeeklyPercent = util.GetDouble();

            if (sevenDay.TryGetProperty("resets_at", out var resets) &&
                resets.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(resets.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                data.WeeklyResetsAt = dt.ToUniversalTime();
            }
        }

        return data;
    }

    private async Task<HttpResponseMessage> SendRequest(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sessionKey))
            throw new AuthenticationException("Kein SessionKey gesetzt.");

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"sessionKey={_sessionKey}");

        var response = await _http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _organizationId = null;
            throw new AuthenticationException("Session abgelaufen.");
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            _organizationId = null;
            throw new AuthenticationException("Session abgelaufen (Redirect).");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("html"))
        {
            _organizationId = null;
            throw new AuthenticationException("Session ungültig (HTML statt JSON).");
        }

        response.EnsureSuccessStatusCode();
        return response;
    }

    public void Dispose() => _http.Dispose();
}

public class AuthenticationException : Exception
{
    public AuthenticationException(string message) : base(message) { }
}
