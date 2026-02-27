# Claude Usage Monitor v2

Windows tray app that shows your Claude.ai usage at a glance.

## How it works

The app reads the OAuth token that **Claude Code** stores in your Windows Credential Manager, then calls the Anthropic OAuth usage API. One HTTP request. No browser, no cookies, no WebView2, no manual configuration.

**Prerequisite:** You need [Claude Code](https://docs.anthropic.com/en/docs/claude-code) installed and logged in (`claude login`).

## Setup

```bash
git clone https://github.com/Firnschnee/Tray-Usage-Monitor.git
cd Tray-Usage-Monitor
dotnet restore
dotnet build -c Release
dotnet run
```

That's it. If you're logged into Claude Code, the tray icon should show your session usage within seconds.

## What you see

- **Tray icon** with session percentage (green/yellow/red)
- **Tooltip** with session %, weekly %, and reset timers
- **Double-click** for detail popup with progress bars
- **Right-click** menu: Details, Refresh, Exit

## How it actually works (technically)

1. Reads `"Claude Code-credentials"` from Windows Credential Manager (or `~/.claude/.credentials.json`)
2. Extracts the `claudeAiOauth.accessToken` 
3. Calls `GET https://api.anthropic.com/api/oauth/usage` with Bearer auth
4. Parses `five_hour`, `seven_day`, and `extra_usage` from JSON response
5. Updates tray icon every 2 minutes

Inspired by [omachala's bash gist](https://gist.github.com/omachala/5ea5af4bfa0b194a1d48d6f2eedd6274) which does the same thing for macOS/CLI.

## Token expired?

Run `claude login` in your terminal. The app picks up the new token automatically on the next poll cycle.

## Project structure

```
├── Program.cs            # Entry point
├── MainForm.cs           # Tray icon, polling, UI
├── UsageFetcher.cs       # Single HTTP call to Anthropic API
├── UsageData.cs          # Data model
└── CredentialReader.cs   # Reads OAuth token from Credential Manager / file
```

Zero external dependencies. Just .NET 8 and `System.Text.Json`.

## License
MIT License – See [LICENSE](LICENSE) file

MIT
