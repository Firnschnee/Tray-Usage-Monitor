# Security & QA Audit Report — ClaudeUsageMonitor

**Date:** 2026-03-02
**Version:** 0.2.0
**Scope:** Full source audit of all `.cs` files
**Severity scale:** Critical / High / Medium / Low / Info

---

## Executive Summary

The codebase is lean (6 source files, zero external dependencies) and generally well-structured. No critical vulnerabilities were found. The most significant recurring issue is a **GDI handle leak** in the tray icon rendering path that will cause handle exhaustion over long runtimes. Several minor security hygiene gaps and QA concerns are documented below.

---

## Security Findings

### SEC-01 — Token Fragment Logged to Debug Output
**File:** `CredentialReader.cs:174`
**Severity:** Medium

```csharp
Log($"accessToken extracted: {val[..Math.Min(20, val.Length)]}...");
```

The first 20 characters of the OAuth access token are written to `Debug.WriteLine`. While not present in Release builds by default, `Debug.WriteLine` output can be captured by any attached debugger or diagnostic tool (e.g., DebugView, WinDbg, Visual Studio). A 20-character token prefix is a meaningful credential fragment.

**Recommendation:** Remove the token value from the log line entirely, or replace with a non-sensitive indicator such as the token length.

---

### SEC-02 — User-Agent Spoofing
**File:** `UsageFetcher.cs:36`
**Severity:** Low

```csharp
req.Headers.Add("User-Agent", "claude-code/2.0.32");
```

The application identifies itself as `claude-code/2.0.32`, impersonating the official Claude Code CLI. This allows the application to use an API endpoint (`/api/oauth/usage`) that may be restricted to that client. If Anthropic changes access control policy for this endpoint, this approach could break silently or result in account flags.

**Recommendation:** Accept as a known design constraint. Document it clearly in the README as a dependency on this undocumented endpoint's behavior.

---

### SEC-03 — Misleading "Copy Raw JSON" Menu Item
**File:** `MainForm.cs:151–158`
**Severity:** Low

```csharp
var raw = new ToolStripMenuItem("Copy Raw JSON");
raw.Click += (_, _) =>
{
    if (_lastData?.TooltipText != null)
        Clipboard.SetText(_lastData.TooltipText);
};
```

The menu item is labeled "Copy Raw JSON" but actually copies the formatted `TooltipText` string (e.g., `"Session: 42% | Reset: 2h 15m"`), not the raw API JSON. This is a misleading label that could confuse users trying to report issues or inspect their data.

**Recommendation:** Rename to "Copy Status Text" or cache the raw JSON response from `UsageFetcher` and copy that instead.

---

### SEC-04 — Predictable Single-Instance Mutex Name
**File:** `Program.cs:8`
**Severity:** Low

```csharp
using var mutex = new Mutex(true, "ClaudeUsageMonitor_v3", out bool isNew);
if (!isNew) { MessageBox.Show("Already running.", "Claude Usage Monitor"); return; }
```

A named mutex with a predictable name (`"ClaudeUsageMonitor_v3"`) is used for single-instance enforcement. Any process running as the same user can pre-create this mutex to prevent the application from starting. This is a known limitation of this pattern.

**Recommendation:** Low priority. For a personal tray tool this is acceptable. If hardening is desired, use a GUID-based mutex name.

---

### SEC-05 — Credential File Path Disclosed in Balloon Notification
**File:** `MainForm.cs:86–98`
**Severity:** Low / Info

```csharp
var credFile = Path.Combine(userProfile, ".claude", ".credentials.json");
var fileExists = File.Exists(credFile);
var diagMsg = $"No OAuth token found.\nFile: {credFile}\nExists: {fileExists}\n...";
_trayIcon.ShowBalloonTip(10000, "Claude Usage Monitor", diagMsg, ToolTipIcon.Warning);
```

When no token is found, the full expanded path (e.g., `C:\Users\Max\.claude\.credentials.json`) is displayed in a Windows balloon notification visible on screen. On shared or screen-recorded machines this could expose the username via the path. This is diagnostic information shown only to the local user, so risk is minimal.

**Recommendation:** Info-level. No change required unless there are screen-sharing concerns.

---

## QA Findings

### QA-01 — GDI Handle Leak in Tray Icon Rendering (Most Significant)
**File:** `MainForm.cs:334, 341`
**Severity:** High

```csharp
// MakeIcon:
return Icon.FromHandle(bmp.GetHicon());

// SetIcon:
var old = _trayIcon.Icon;
_trayIcon.Icon = MakeIcon(text, color);
old?.Dispose();
```

`Bitmap.GetHicon()` creates a GDI icon handle (HICON). `Icon.FromHandle()` wraps it in a managed `Icon` object, but per Microsoft documentation, when an `Icon` is created via `FromHandle`, calling `Icon.Dispose()` does **not** release the underlying HICON — the caller is responsible for calling `DestroyIcon()`. Since `old?.Dispose()` does not free the HICON, every poll cycle (~every 2 minutes, or every 1 minute while the detail widget is open) leaks one GDI handle. Over a multi-hour session (e.g., 8 hours × 60 polls) this accumulates ~60 leaked HICONs. Windows has a per-process GDI handle limit of 10,000; sustained use would eventually cause rendering failures.

**Recommendation:** After replacing the icon, explicitly free the old HICON:

```csharp
// In SetIcon, after old?.Dispose():
// Store the raw HICON before wrapping, and DestroyIcon it on replacement.
```

This requires storing the HICON handle alongside the Icon object, or switching to `new Icon(bmp, sz, sz)` which does not use GetHicon.

---

### QA-02 — No Cancellation on In-Flight HTTP Request at Shutdown
**File:** `MainForm.cs:104`, `UsageFetcher.cs:38`
**Severity:** Low

```csharp
var data = await _fetcher.FetchAsync(token); // no CancellationToken
```

When the app exits while a fetch is in progress (e.g., the user right-clicks → Exit during a poll), the HTTP request cannot be cancelled. The `HttpClient` has a 15-second timeout, so the disposal of `_fetcher` may be delayed.

**Recommendation:** Thread a `CancellationTokenSource` through `PollAsync` and signal it from `Dispose`.

---

### QA-03 — Detail Widget Halves the Effective Poll Interval
**File:** `MainForm.cs:218`
**Severity:** Low / Info

```csharp
_widgetTimer = new System.Windows.Forms.Timer { Interval = 60_000 }; // 1 min
_widgetTimer.Tick += async (_, _) => await PollAsync();
```

When the detail popup is visible, both `_pollTimer` (2-minute interval) and `_widgetTimer` (1-minute interval) fire independently. The `_polling` guard prevents concurrent fetches, but the effective refresh rate becomes ~1 minute rather than 2 minutes. This is undocumented behavior that doubles API call frequency while the window is open.

**Recommendation:** Remove `_widgetTimer`'s poll tick handler and rely solely on `_pollTimer`. The widget already calls `PollAsync()` on open (line 180 and 225).

---

### QA-04 — Unhandled Exceptions Silently Discarded in Production
**File:** `Program.cs:15–16`
**Severity:** Low

```csharp
Application.ThreadException += (_, e) => System.Diagnostics.Debug.WriteLine($"UI: {e.Exception}");
AppDomain.CurrentDomain.UnhandledException += (_, e) => System.Diagnostics.Debug.WriteLine($"Fatal: {e.ExceptionObject}");
```

Both exception handlers write only to `Debug.WriteLine`, which produces no output in a Release build without a debugger attached. If an unhandled exception occurs in production, the app may silently crash or hang with no user feedback and no diagnostic information.

**Recommendation:** For `ThreadException`, show a `MessageBox` to the user. For `UnhandledException`, write to a log file in `%TEMP%`.

---

### QA-05 — Null-Forgiving Operator on PrimaryScreen
**File:** `MainForm.cs:196`
**Severity:** Low

```csharp
var screen = Screen.PrimaryScreen!.WorkingArea;
```

`Screen.PrimaryScreen` can theoretically return `null` in headless or unusual display configurations. The `!` operator suppresses the nullable warning but does not guard against a `NullReferenceException`.

**Recommendation:** Add a null guard: `Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080)`.

---

### QA-06 — P/Invoke Calls Without SetLastError
**File:** `Win32Interop.cs:74–122`
**Severity:** Info

Most P/Invoke declarations (`SetParent`, `MoveWindow`, `GetWindowLong`, `SetWindowLong`, etc.) do not specify `SetLastError = true`. When these calls fail, there is no way to retrieve the Win32 error code, making taskbar embedding failures difficult to diagnose.

**Recommendation:** Add `SetLastError = true` to functions whose return value indicates failure, particularly `SetParent`, `MoveWindow`, `GetWindowRect`, and `SetWindowLong`.

---

### QA-07 — Mixed Local / UTC Time in Data Model
**File:** `UsageData.cs:28, 66–70`
**Severity:** Info

```csharp
public DateTime FetchedAt { get; set; } = DateTime.Now;       // local time
public DateTime? SessionResetsAt { get; set; }                 // stored as UTC
```

`FetchedAt` uses local time while `SessionResetsAt` / `WeeklyResetsAt` are UTC (converted correctly in `TimeUntil`). The model mixes time zones, which is technically correct in usage but risks confusion if fields are added in future and the convention is not followed.

**Recommendation:** Document the convention with a comment on `FetchedAt`, or normalize everything to UTC internally.

---

### QA-08 — Full JSON Response Logged in Debug Mode
**File:** `UsageFetcher.cs:46`
**Severity:** Info

```csharp
System.Diagnostics.Debug.WriteLine($"[Usage] Response: {json}");
```

The complete API response body (including all utilization figures) is written to the Debug output stream. Acceptable for development, but could expose usage pattern data if a debugger/trace tool is attached.

**Recommendation:** Consider wrapping in `#if DEBUG` to make the intent explicit.

---

## Summary Table

| ID | File | Description | Severity |
|----|------|-------------|----------|
| SEC-01 | CredentialReader.cs:174 | OAuth token prefix logged to Debug output | ~~Medium~~ **Fixed** |
| SEC-02 | UsageFetcher.cs:36 | User-Agent spoofs claude-code CLI | Low |
| SEC-03 | MainForm.cs:151 | "Copy Raw JSON" copies tooltip text, not JSON | ~~Low~~ **Fixed** |
| SEC-04 | Program.cs:8 | Predictable single-instance mutex name | ~~Low~~ **Fixed** |
| SEC-05 | MainForm.cs:89 | Credential file path in balloon notification | ~~Low~~ **Fixed** |
| QA-01 | MainForm.cs:334 | GDI HICON handle leak per poll cycle | **High** |
| QA-02 | MainForm.cs:104 | No CancellationToken on HTTP fetch at shutdown | Low |
| QA-03 | MainForm.cs:218 | Widget timer doubles API call rate | Low |
| QA-04 | Program.cs:15 | Unhandled exceptions silently swallowed | Low |
| QA-05 | MainForm.cs:196 | Null-forgiving on Screen.PrimaryScreen | Low |
| QA-06 | Win32Interop.cs | P/Invoke lacks SetLastError on key calls | Info |
| QA-07 | UsageData.cs:28 | Mixed local/UTC time convention | Info |
| QA-08 | UsageFetcher.cs:46 | Full response body logged in Debug | Info |

---

## What Was Not Found

- No SQL injection, XSS, or injection vulnerabilities (no web server, no dynamic query construction)
- No hardcoded credentials or API keys
- No insecure deserialization (uses `System.Text.Json` with default settings, no polymorphic deserialization)
- No TLS downgrade or certificate validation bypass (`HttpClient` uses system cert store)
- No command injection (no shell execution, no process spawning)
- No resource access beyond the explicit design intent (reads `~/.claude/.credentials.json`, queries `api.anthropic.com`, reads registry for theme preference)
- GDI resource cleanup in `TaskbarWidget.Paint` is correct and complete

---

*Report generated by manual code review. No automated scanning tools were used.*
