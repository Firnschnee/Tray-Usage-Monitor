## Claude Usage Monitor - Taskbar Widget + Popup Window 

A Windows tray app that shows your Claude.ai usage at a glance - including a widget embedded directly in the taskbar. 

<img width="699" height="47" alt="taskbar" src="https://github.com/user-attachments/assets/3312f88c-9564-4503-83cf-096a7a72ef35" />

## How it works

The app reads the OAuth token that **Claude Code** stores in your Windows Credential Manager, then calls the Anthropic OAuth usage API. One HTTP request. No browser, no cookies, no WebView2, no manual configuration.

## Requirements:
You need [Claude Code](https://docs.anthropic.com/en/docs/claude-code) installed and logged in (`claude login`) 
and .NET 8 - zero external dependencies. 

## Setup

Use the `ClaudeUsageMonitor.exe` from the latest release 

Or build it yourself: 

```bash
git clone https://github.com/Firnschnee/Tray-Usage-Monitor.git
cd Tray-Usage-Monitor
dotnet build -c Release
dotnet run
```

That's it. If you're logged into Claude Code, the tray icon should show your session usage within seconds.

## What you see

- **Taskbar widget** — embedded next to the system tray, always visible. Shows two progress bars (`5h` session + `7d` weekly) with percentage and countdown, green/yellow/red by utilization. Updates automatically when the displayed countdown changes.
- **Tray icon** with session percentage (green/yellow/red)
- **Tooltip** with session %, weekly % (with pace), and reset timers
- **Right-click** menu: Details, Refresh, Copy Raw JSON, Exit
- **Popup window** — opens on startup and on double-click, always on top. Three progress bars: `5h` session, `7d` weekly, `extra usage` monthly — with colored pace markers and subtitles (e.g. Reset: 1h 23m | +12% ahead)

<img width="402" height="235" alt="Tray Usage Monitor" src="https://github.com/user-attachments/assets/0479ef8d-bcb8-445e-9b56-df71c411852c" />

If taskbar embedding isn't available (unsupported shell, modified taskbar), the app falls back gracefully to tray icon + popup only.

### Don't want the popup? 
Either close it or - there is now a branch without the popup! 

## How it actually works (technically)

1. Reads `"Claude Code-credentials"` from Windows Credential Manager, then falls back to `%USERPROFILE%\.claude\.credentials.json` (and `%HOMEDRIVE%%HOMEPATH%\.claude\` as a second fallback)
2. Extracts the `claudeAiOauth.accessToken` 
3. Calls `GET https://api.anthropic.com/api/oauth/usage` with Bearer auth
4. Parses `five_hour`, `seven_day`, and `extra_usage` from JSON response
5. Updates tray icon every 2 minutes (every 60 seconds while the widget is open)

Inspired by [omachala's bash gist](https://gist.github.com/omachala/5ea5af4bfa0b194a1d48d6f2eedd6274) which does the same thing for macOS/CLI.

## Token expired?

Run `claude login` in your terminal. The app picks up the new token automatically on the next poll cycle.

## Project structure

```
├── Program.cs            # Entry point
├── MainForm.cs           # Tray icon, polling, UI orchestration
├── TaskbarWidget.cs      # Taskbar-embedded widget (Win32 reparenting + layered window)
├── Win32Interop.cs       # P/Invoke declarations for Win32 APIs
├── UsageFetcher.cs       # Single HTTP call to Anthropic API
├── UsageData.cs          # Data model
└── CredentialReader.cs   # Reads OAuth token from Credential Manager / file
```

## License
MIT License – See [LICENSE](LICENSE) file
