# Claude Usage Monitor – Windows Tray App

A Windows tray application for monitoring Claude.ai usage limits in real-time.

**Status:** *In Development* – Authentication works, API data retrieval needs debugging.

## The Problem

You have a 5-hour session limit and a 7-day weekly limit on Claude. You're constantly wondering: "How much do I have left?" So you alt-tab to settings, check the usage page, come back. Repeat 10 times a day.

This app sits in your system tray and shows you at a glance: How many hours left in your session. How many in your weekly limit. That's it.

## What Works ✅

- **WebView2-based login** – Real browser window embedded in the app. Log in to claude.ai like normal. No manual cookie copying, no DevTools nonsense.
- **Session storage** – Your session is saved locally, encrypted with Windows DPAPI. No plain-text passwords.
- **Tray icon** – Minimal, always visible. Click for details.

## What Doesn't Work (Yet) ❌

- **API data retrieval** – The app can authenticate, but retrieving actual usage data from `/api/organizations/{orgId}/usage` fails unreliably
- **Session refresh logic** – Sometimes reports session expired even though you're logged in

**Why?** Likely causes under investigation:
- Missing/incorrect HTTP headers in API requests
- Session expiry detection too aggressive
- Possible API endpoint changes on Anthropic's side
- CORS or authentication token issues

## How It Should Work (Eventually)

1. Start the app → WebView2 window opens with claude.ai login
2. Log in normally (Google, email, SSO — whatever you use)
3. Window closes automatically after successful login
4. Tray icon updates every 2 minutes with your usage
5. Right-click menu: View details, refresh, settings, exit

No configuration. No cookie headers. Just login and forget it's running.

## Setup

### Requirements
- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (pre-installed on Windows 11, may need manual install on Windows 10)

### Build & Run
```bash
git clone https://github.com/yourusername/claude-usage-monitor
cd claude-usage-monitor
dotnet restore
dotnet build -c Release
dotnet run
```

## How It Works (Technically)

The app uses the same internal API endpoints as the claude.ai web interface:
```
GET /api/organizations                    → Fetch your organization ID
GET /api/organizations/{orgId}/usage      → Fetch usage data
```

Expected response:
```json
{
  "five_hour": {
    "utilization": 42.5,
    "resets_at": "2025-02-17T18:00:00Z"
  },
  "seven_day": {
    "utilization": 13.0,
    "resets_at": "2025-02-19T07:00:00Z"
  }
}
```

**Current blocker:** The API calls fail after successful authentication, reason unknown.

## Project Structure
```
claude-usage-monitor/
├── Program.cs              # Entry point, singleton app lifecycle
├── MainForm.cs             # Tray icon, polling loop, context menu
├── LoginForm.cs            # WebView2 login window + session capture
├── ClaudeApiClient.cs      # HTTP client for /api/organizations + /usage
├── UsageData.cs            # Data model for five_hour / seven_day
└── AppSettings.cs          # Settings & DPAPI-encrypted session storage
```

## Contributing

If you know why the API calls are failing after successful login, PRs are welcome. Open an issue with:
- What you tried
- The exact error message
- HTTP headers you're sending
- Response status code

## License

MIT

---

**Built because I got tired of alt-tabbing to check my Claude limits.**
