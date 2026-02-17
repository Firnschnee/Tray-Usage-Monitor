## Tray-Usage-Monitor
Trying to build a claude.ai usage monitor for Windodws. 
*So far I failed miserably. Twice.* 

This is my second try of building a claude.ai usage monitor. 
The first try failed horribly. 
This is my second try. It still fails. And I dont know why. 
I just uploaded everything for myself later - when I hopefully have more vigor to tackle the bugs. 

Anyway, here is the usual README:

## My Goal? 
A Windows tray application for monitoring Claude.ai usage limits.

## Changes from v1 to v2? 
**WebView2-based login** - no more manual cookie header copying. The app now opens a real browser window (Chromium via WebView2), you log in to claude.ai as usual, and the app automatically captures the session. 
*At least thats the idea. I cannot for the love of god get this thing to work reliable*

## Features (theoretically) 
- **Automatic login** via embedded browser (WebView2)
- **Silent re-login** when session expires (uses existing OAuth session)
- **Tray icon** with color-coded percentage display
- **Session + Weekly** usage tracking (5h / 7-day window)
- **Settings**: Polling interval, warning threshold, autostart
- **DPAPI-encrypted** credential local storage

## Requirements
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (pre-installed on Windows 11, may need to be installed manually on Windows 10)

## Build & Run
```bash
dotnet restore
dotnet build -c Release
dotnet run
```

## Initial setup
1. Start the app → Browser window opens with claude.ai
2. Log in as usual (Google, email, SSO — everything works)
3. Window closes automatically after successful login
4. Usage data appears in the tray icon
**That's it.** No cookie copying, no DevTools.

## API
The app uses the same internal endpoints as the claude.ai web app:
```
GET /api/organizations                        → Determine org ID
GET /api/organizations/{orgId}/usage          → Usage data (JSON)
```

Response format:
```json
{
  “five_hour”: { “utilization”: 42.5, “resets_at”: “2025-02-17T18:00:00Z” },
  “seven_day”: { “utilization”: 13.0, ‘resets_at’: “2025-02-19T07:00:00Z” }
}
```

## Architecture
```
ClaudeUsageMonitor/
├── Program.cs          # Entry Point, Singleton
├── MainForm.cs         # Tray Icon, Polling, Context Menu, Dialogs
├── LoginForm.cs        # WebView2 Browser Login + Automatic Cookie Capture
├── ClaudeApiClient.cs  # HTTP client for /api/organizations + /usage
├── UsageData.cs        # Data model (five_hour / seven_day)
└── AppSettings.cs      # JSON settings + DPAPI session storage
```
