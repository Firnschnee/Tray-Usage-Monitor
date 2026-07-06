# Usage Dashboard Design (Ansatz 1)

Approved by Max on 2026-07-06 (chat). Reference points: CodexBar (feature ideas),
CodeZeno/Claude-Code-Usage-Monitor (scope comparison).

## Goal

Extend the tray/taskbar usage monitor with (a) burn-rate forecasting across all
Claude channels, (b) local Claude Code token/cost detail from JSONL logs, and
(c) a click-to-open popup dashboard plus notifications and basic settings.

## Scope

**In:**
- Sample persistence of every poll; linear burn-rate forecast per limit window
- Popup dashboard (borderless dark panel) on left-click of taskbar widget or tray icon
- "Claude Code (local)" section: tokens today/this week, per-model split, API-equivalent cost
- Parse `seven_day_opus` from the OAuth usage API
- Threshold notifications (warn/crit, once per window instance)
- Settings: poll interval, notifications toggle, autostart; persisted to JSON; UI via tray context menu
- Test project for the pure logic (forecast, JSONL parsing, settings)

**Out (explicitly):**
- Additional providers (Codex, Gemini, ...) — Max uses Claude only
- History charts (Ansatz 3, later; the 14-day sample retention is its data supply)
- Token-level visibility into claude.ai web usage — no such data source exists;
  the OAuth percentages are the only account-wide truth

## Data sources

| Source | Covers | Granularity |
|---|---|---|
| `GET /api/oauth/usage` | all channels (web + Code + desktop) | percent per window: `five_hour`, `seven_day`, `seven_day_opus`, `extra_usage` |
| `~/.claude/projects/**/*.jsonl` | Claude Code only | per assistant message: `message.usage.{input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens}`, `message.model`, `message.id` (dedup key), top-level `timestamp` (UTC ISO) |

## Components (one file per class, no external runtime deps)

| File | Responsibility |
|---|---|
| `AppSettings.cs` | Settings model + JSON persistence in `%APPDATA%\ClaudeUsageMonitor\settings.json`; autostart via HKCU Run key |
| `UsageHistory.cs` | `UsageSample` record, 14-day sample store (`history.json`), static least-squares `Forecast()` |
| `JsonlUsageReader.cs` | JSONL scan + aggregation (`ModelUsage`, `LocalUsageReport`), embedded pricing table |
| `PopupForm.cs` | Dashboard panel; account section with forecast lines, local-usage section (async load, 5-min cache) |

Modified: `UsageData.cs` / `UsageFetcher.cs` (opus window), `MainForm.cs` (wiring,
notifications, menu, popup; old details window removed), `TaskbarWidget.cs`
(left-click), `ClaudeUsageMonitor.csproj` (InternalsVisibleTo, version).

## Forecast algorithm

- Input: samples within the current limit window (`resets_at` minus window length),
  additionally capped to a lookback (session: 1 h, weekly/opus: 24 h).
- Require ≥ 3 samples spanning ≥ 10 minutes, else "gathering data".
- Least-squares slope in %/hour. Slope ≤ 0.01 → "ok until reset".
- Else `limitAt = now + (100 − current)/slope`; shown only if before `resets_at`,
  otherwise "ok until reset".
- Deliberately linear extrapolation; anything fancier would fake precision.
- Samples carrying the -1 "window not reported" sentinel are excluded from the fit.

## Popup layout (top to bottom)

1. Header "Account usage (all channels)"
2. Session (5h) bar + reset/pace line + forecast line
3. Weekly (7d) bar + reset/pace line + forecast line
4. Opus (7d) bar if the API reports it; Extra usage bar if enabled
5. Separator, header "Claude Code (local logs)"
6. Today / This week token totals with ~$ API-equivalent, top-3 models
7. Footer: fetch time

Behavior: opens above the tray area, `TopMost`, closes on focus loss or Escape.
Left-click on tray icon or taskbar widget toggles it. Replaces the old 400×60
details window (`ShowDetails`), which becomes redundant.

## Notifications

Balloon tips at warn (75 %) and crit (90 %) thresholds for session and weekly,
at most once per threshold per window instance; the marker resets when
`resets_at` changes. Thresholds configurable in `settings.json`; enable/disable
in the tray menu.

## Error handling

- Broken JSONL lines: skipped. Missing projects dir: "No local Claude Code data found."
- Corrupt `settings.json` / `history.json`: silently replaced with defaults/empty.
- JSONL files are opened with `FileShare.ReadWrite | Delete` (Claude Code appends live).
- Nothing in the new code may break the poll cycle; local-usage scan runs off the UI thread.

## Testing

New `Tests/ClaudeUsageMonitor.Tests.csproj` (xunit; dev-only dependency, the app
itself stays dependency-free). Covered: settings roundtrip/sanitizing, history
persistence/pruning, forecast math, API response parsing incl. opus, JSONL line
parsing/dedup/aggregation/pricing. UI (popup, widget click, menu) is verified
manually. Main build command stays `dotnet build -c Release`; tests run via
`dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`.
