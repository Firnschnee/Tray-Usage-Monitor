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

- **Taskbar widget** – embedded next to the system tray, always visible. Shows two progress bars (`5h` session + `7d` weekly) with percentage and countdown, green/yellow/red by utilization. Updates automatically when the displayed countdown changes.
- **Tray icon** with session percentage (green/yellow/red)
- **Tooltip** with session %, weekly % (with pace), and reset timers
- **Popup dashboard** – left-click the tray icon or the taskbar widget to open it (Escape or clicking elsewhere closes it). Two sections:
  - **Account usage (all channels)**: progress bars for `5h` session, `7d` weekly, `7d` Opus (when your plan reports it) and extra usage, each with reset countdown and pace marker. The session and weekly rows also show a burn-rate forecast (when you'd hit 100% at the current pace), computed from a least-squares fit over the recently polled percentages. The forecast reads "gathering data" until enough samples exist – at least 3 polls spanning 10+ minutes within the current window.
  - **Claude Code (local logs)**: token counts and API-equivalent cost for today and the current week, plus your most-used models, read from the local `~/.claude/projects` JSONL logs (cached for 5 minutes).
- **Threshold notifications** – a balloon tip once at 75% and once at 90% per limit window, re-armed when the window resets. Can be switched off.
- **Right-click** menu: Details, Refresh, Copy Raw JSON, Settings (poll interval 1/2/5/15 min, notifications on/off, start with Windows), Exit

One honest caveat: the account-wide percentages cover your usage on all channels (claude.ai web included), but the token/cost breakdown covers Claude Code only – claude.ai web usage never appears in the local JSONL logs, so there is no per-token detail for it.

<img width="402" height="235" alt="Tray Usage Monitor" src="https://github.com/user-attachments/assets/0479ef8d-bcb8-445e-9b56-df71c411852c" />

If taskbar embedding isn't available (unsupported shell, modified taskbar), the app falls back gracefully to tray icon + popup only.

## How it actually works (technically)

1. Reads `"Claude Code-credentials"` from Windows Credential Manager, then falls back to `%USERPROFILE%\.claude\.credentials.json` (and `%HOMEDRIVE%%HOMEPATH%\.claude\` as a second fallback)
2. Extracts the `claudeAiOauth.accessToken` 
3. Calls `GET https://api.anthropic.com/api/oauth/usage` with Bearer auth
4. Parses `five_hour`, `seven_day`, `seven_day_opus`, and `extra_usage` from JSON response
5. Updates tray icon every 2 minutes by default (poll interval configurable to 1/2/5/15 min; settings persist to `%APPDATA%\ClaudeUsageMonitor\settings.json`)

Inspired by [omachala's bash gist](https://gist.github.com/omachala/5ea5af4bfa0b194a1d48d6f2eedd6274) which does the same thing for macOS/CLI.

## Token expired?

Run `claude login` in your terminal. The app picks up the new token automatically on the next poll cycle.

## Project structure

```
├── Program.cs            # Entry point
├── MainForm.cs           # Tray icon, polling, UI orchestration
├── TaskbarWidget.cs      # Taskbar-embedded widget (Win32 reparenting + layered window)
├── PopupForm.cs          # Popup dashboard (account usage + local Claude Code stats)
├── UsageHistory.cs       # Polled samples + least-squares burn-rate forecast
├── JsonlUsageReader.cs   # Local Claude Code token/cost stats from ~/.claude/projects JSONL
├── AppSettings.cs        # Settings persistence (%APPDATA%\ClaudeUsageMonitor\settings.json)
├── Win32Interop.cs       # P/Invoke declarations for Win32 APIs
├── UsageFetcher.cs       # Single HTTP call to Anthropic API
├── UsageData.cs          # Data model
└── CredentialReader.cs   # Reads OAuth token from Credential Manager / file
```

## License
MIT License – See [LICENSE](LICENSE) file
