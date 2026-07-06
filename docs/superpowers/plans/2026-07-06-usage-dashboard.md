# Usage Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Burn-rate forecast across all Claude channels, local Claude Code token/cost detail, popup dashboard, notifications, settings.

**Architecture:** New pure-logic classes (`AppSettings`, `UsageHistory`, `JsonlUsageReader`) covered by a new xunit test project; new `PopupForm` UI; `MainForm` wires polling → history → notifications → popup and replaces the old details window. `TaskbarWidget` gains a left-click callback.

**Tech Stack:** C# .NET 8 WinForms, `System.Text.Json`, xunit (test-only). No runtime dependencies.

## Global Constraints

- No external runtime dependencies in `ClaudeUsageMonitor.csproj` (test project may use xunit + Microsoft.NET.Test.Sdk).
- One file per class, namespace `ClaudeUsageMonitor`, file-scoped namespaces, 4-space indent (match existing files).
- Build command must stay `dotnet build -c Release` from repo root → do NOT create a `.sln` (a sln next to the csproj makes that command ambiguous). Tests run via `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`.
- Data source stays `GET https://api.anthropic.com/api/oauth/usage`; polling stays in `MainForm.cs`; widget is fed via `Update(data)`.
- UI language: English (matches existing labels). No em-dash (U+2014) anywhere, including commits; use en-dash or colon.
- Colors: reuse existing palette (`#22C55E` ok, `#FBBF24` warn, `#EF4444` crit, `#18181B` bg, `#D97757` accent).
- Settings/history live in `%APPDATA%\ClaudeUsageMonitor\` (`settings.json`, `history.json`).

---

### Task 1: Test infrastructure

**Files:**
- Create: `Tests/ClaudeUsageMonitor.Tests.csproj`
- Modify: `ClaudeUsageMonitor.csproj` (add InternalsVisibleTo)
- Create: `Tests/SmokeTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: a test project all later tasks add test files to; internals of the main assembly visible to `ClaudeUsageMonitor.Tests`

- [ ] **Step 1: Create test project file**

`Tests/ClaudeUsageMonitor.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ClaudeUsageMonitor.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Expose internals to the test assembly**

In `ClaudeUsageMonitor.csproj`, after the `<PropertyGroup>`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="ClaudeUsageMonitor.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Smoke test**

`Tests/SmokeTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public class SmokeTests
{
    [Fact]
    public void MainAssemblyIsReferenced()
    {
        var data = new UsageData { SessionPercent = 42 };
        Assert.Equal(42, data.SessionPercent);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: 1 passed.

- [ ] **Step 5: Verify root build still works**

Run: `dotnet build -c Release`
Expected: succeeds, exactly one project picked up.

- [ ] **Step 6: Commit**

```bash
git add Tests/ClaudeUsageMonitor.Tests.csproj Tests/SmokeTests.cs ClaudeUsageMonitor.csproj
git commit -m "Add xunit test project"
```

---

### Task 2: AppSettings

**Files:**
- Create: `AppSettings.cs`
- Test: `Tests/AppSettingsTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `AppSettings` with `int PollIntervalMinutes` (default 2, allowed 1/2/5/15), `bool NotificationsEnabled` (default true), `int WarnThresholdPercent` (75), `int CritThresholdPercent` (90); `static AppSettings Load()`, `void Save()`, `static string SettingsDir { get; set; }` (settable for tests), `static bool IsAutostartEnabled()`, `static void SetAutostart(bool)`

- [ ] **Step 1: Write the failing tests**

`Tests/AppSettingsTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _dir;

    public AppSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cum-tests-" + Guid.NewGuid().ToString("N"));
        AppSettings.SettingsDir = _dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void LoadWithoutFileReturnsDefaults()
    {
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);
        Assert.True(s.NotificationsEnabled);
        Assert.Equal(75, s.WarnThresholdPercent);
        Assert.Equal(90, s.CritThresholdPercent);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        var s = new AppSettings { PollIntervalMinutes = 5, NotificationsEnabled = false, WarnThresholdPercent = 60, CritThresholdPercent = 80 };
        s.Save();
        var loaded = AppSettings.Load();
        Assert.Equal(5, loaded.PollIntervalMinutes);
        Assert.False(loaded.NotificationsEnabled);
        Assert.Equal(60, loaded.WarnThresholdPercent);
        Assert.Equal(80, loaded.CritThresholdPercent);
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{not json");
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);
    }

    [Fact]
    public void InvalidValuesAreSanitized()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"PollIntervalMinutes":7,"WarnThresholdPercent":95,"CritThresholdPercent":10}""");
        var s = AppSettings.Load();
        Assert.Equal(2, s.PollIntervalMinutes);          // 7 is not an allowed interval
        Assert.Equal(95, s.WarnThresholdPercent);
        Assert.True(s.CritThresholdPercent >= s.WarnThresholdPercent); // crit clamped up to warn
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: FAIL to compile ("AppSettings not found").

- [ ] **Step 3: Implement**

`AppSettings.cs`:

```csharp
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeUsageMonitor;

/// <summary>
/// User settings, persisted as JSON in %APPDATA%\ClaudeUsageMonitor\settings.json.
/// Autostart is not part of the JSON; it lives in the HKCU Run key.
/// </summary>
public sealed class AppSettings
{
    public int PollIntervalMinutes { get; set; } = 2;
    public bool NotificationsEnabled { get; set; } = true;
    public int WarnThresholdPercent { get; set; } = 75;
    public int CritThresholdPercent { get; set; } = 90;

    // Settable so tests can redirect persistence to a temp dir.
    public static string SettingsDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageMonitor");

    private static string FilePath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null) return Sanitize(s);
            }
        }
        catch { /* corrupt file: fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort; in-memory settings stay valid */ }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        if (s.PollIntervalMinutes is not (1 or 2 or 5 or 15)) s.PollIntervalMinutes = 2;
        s.WarnThresholdPercent = Math.Clamp(s.WarnThresholdPercent, 1, 99);
        s.CritThresholdPercent = Math.Clamp(s.CritThresholdPercent, s.WarnThresholdPercent, 100);
        return s;
    }

    // ── Autostart (HKCU Run key) ────────────────────────────────────────────

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClaudeUsageMonitor";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled) key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue(RunValueName, false);
    }
}
```

Note: the registry helpers are NOT unit-tested (they touch real HKCU); they are verified manually in Task 10.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add AppSettings.cs Tests/AppSettingsTests.cs
git commit -m "Add AppSettings with JSON persistence and autostart helpers"
```

---

### Task 3: Opus window parsing (UsageData + UsageFetcher)

**Files:**
- Modify: `UsageData.cs`
- Modify: `UsageFetcher.cs` (make `Parse` internal, add `seven_day_opus`)
- Test: `Tests/UsageFetcherParseTests.cs`

**Interfaces:**
- Consumes: existing `UsageData`, `UsageFetcher`
- Produces: `UsageData.HasOpus` (bool), `OpusPercent` (double), `OpusResetsAt` (DateTime?), `OpusResetText` (string), `SessionWindowStartUtc` (DateTime?), `WeeklyWindowStartUtc` (DateTime?); `internal static UsageData UsageFetcher.Parse(string json)`

- [ ] **Step 1: Write the failing tests**

`Tests/UsageFetcherParseTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public class UsageFetcherParseTests
{
    private const string FullJson = """
        {
          "five_hour":  { "utilization": 42.5, "resets_at": "2026-07-06T18:00:00Z" },
          "seven_day":  { "utilization": 13.0, "resets_at": "2026-07-09T07:00:00Z" },
          "seven_day_opus": { "utilization": 5.5, "resets_at": "2026-07-09T07:00:00Z" },
          "extra_usage": { "is_enabled": false, "monthly_limit": 0, "used_credits": 0, "utilization": 0 }
        }
        """;

    [Fact]
    public void ParsesExistingWindows()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.Equal(42.5, d.SessionPercent);
        Assert.True(d.HasWeekly);
        Assert.Equal(13.0, d.WeeklyPercent);
        Assert.Equal(new DateTime(2026, 7, 6, 18, 0, 0, DateTimeKind.Utc), d.SessionResetsAt);
    }

    [Fact]
    public void ParsesOpusWindow()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.True(d.HasOpus);
        Assert.Equal(5.5, d.OpusPercent);
        Assert.Equal(new DateTime(2026, 7, 9, 7, 0, 0, DateTimeKind.Utc), d.OpusResetsAt);
    }

    [Fact]
    public void MissingOpusMeansHasOpusFalse()
    {
        var d = UsageFetcher.Parse("""{ "five_hour": { "utilization": 10, "resets_at": "2026-07-06T18:00:00Z" } }""");
        Assert.False(d.HasOpus);
        Assert.False(d.HasWeekly);
    }

    [Fact]
    public void WindowStartsAreResetMinusWindowLength()
    {
        var d = UsageFetcher.Parse(FullJson);
        Assert.Equal(new DateTime(2026, 7, 6, 13, 0, 0, DateTimeKind.Utc), d.SessionWindowStartUtc);
        Assert.Equal(new DateTime(2026, 7, 2, 7, 0, 0, DateTimeKind.Utc), d.WeeklyWindowStartUtc);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: FAIL to compile (`Parse` inaccessible, `HasOpus` missing).

- [ ] **Step 3: Implement**

In `UsageData.cs`, after the weekly properties (line ~20):

```csharp
    // Opus weekly window (seven_day_opus) — present on Max/Pro plans with an Opus cap
    public bool HasOpus { get; set; }
    public double OpusPercent { get; set; }
    public DateTime? OpusResetsAt { get; set; }
```

After the existing computed properties (`WeeklyResetText`):

```csharp
    public TimeSpan OpusResetIn => TimeUntil(OpusResetsAt);
    public string OpusResetText => FormatSpan(OpusResetIn);

    // Window start times (UTC) — used by the forecast to ignore pre-reset samples
    public DateTime? SessionWindowStartUtc => SessionResetsAt?.Subtract(SessionWindow);
    public DateTime? WeeklyWindowStartUtc => WeeklyResetsAt?.Subtract(WeeklyWindow);
```

In `UsageFetcher.cs`: change `private static UsageData Parse(string json)` to `internal static UsageData Parse(string json)`, and after the `seven_day` block insert:

```csharp
        // seven_day_opus (Opus weekly cap, same shape as seven_day)
        if (root.TryGetProperty("seven_day_opus", out var sdo))
        {
            data.HasOpus = true;
            if (sdo.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number) data.OpusPercent = u.GetDouble();
            if (sdo.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String)
                if (DateTime.TryParse(r.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    data.OpusResetsAt = dt.ToUniversalTime();
        }
```

(Reuse the local variable names `u` and `r` only if the compiler allows the scopes; otherwise name them `uo` and `ro`.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add UsageData.cs UsageFetcher.cs Tests/UsageFetcherParseTests.cs
git commit -m "Parse seven_day_opus window; expose window start times"
```

---

### Task 4: UsageHistory sample store

**Files:**
- Create: `UsageHistory.cs`
- Test: `Tests/UsageHistoryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `record UsageSample(DateTime TimestampUtc, double SessionPercent, double WeeklyPercent, double OpusPercent)` (percent −1 = window not present); `UsageHistory(string filePath)`, `void Add(UsageSample)`, `IReadOnlyList<UsageSample> Samples`. Forecast API is added in Task 5.

- [ ] **Step 1: Write the failing tests**

`Tests/UsageHistoryTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public sealed class UsageHistoryTests : IDisposable
{
    private readonly string _file;

    public UsageHistoryTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "cum-hist-" + Guid.NewGuid().ToString("N"), "history.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_file)!, true); } catch { }
    }

    [Fact]
    public void AddPersistsAndReloads()
    {
        var h = new UsageHistory(_file);
        h.Add(new UsageSample(new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc), 10, 20, -1));

        var reloaded = new UsageHistory(_file);
        Assert.Single(reloaded.Samples);
        Assert.Equal(10, reloaded.Samples[0].SessionPercent);
        Assert.Equal(-1, reloaded.Samples[0].OpusPercent);
    }

    [Fact]
    public void PrunesSamplesOlderThan14Days()
    {
        var now = new DateTime(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);
        var h = new UsageHistory(_file);
        h.Add(new UsageSample(now.AddDays(-15), 1, 1, -1));
        h.Add(new UsageSample(now.AddDays(-13), 2, 2, -1));
        h.Add(new UsageSample(now, 3, 3, -1));
        Assert.Equal(2, h.Samples.Count);
        Assert.DoesNotContain(h.Samples, s => s.SessionPercent == 1);
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "not json at all");
        var h = new UsageHistory(_file);
        Assert.Empty(h.Samples);
        h.Add(new UsageSample(DateTime.UtcNow, 5, 5, -1)); // must not throw
        Assert.Single(h.Samples);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`UsageHistory.cs`:

```csharp
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>One poll snapshot. Percent value −1 means the window was not reported.</summary>
public sealed record UsageSample(DateTime TimestampUtc, double SessionPercent, double WeeklyPercent, double OpusPercent);

/// <summary>
/// Persistent store of poll samples (JSON array in %APPDATA%\ClaudeUsageMonitor\history.json).
/// Keeps 14 days: enough for weekly forecasts and the data supply for future history charts.
/// </summary>
public sealed class UsageHistory
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private readonly string _filePath;
    private readonly List<UsageSample> _samples = new();

    public IReadOnlyList<UsageSample> Samples => _samples;

    public UsageHistory(string filePath)
    {
        _filePath = filePath;
        try
        {
            if (File.Exists(filePath))
            {
                var loaded = JsonSerializer.Deserialize<List<UsageSample>>(File.ReadAllText(filePath));
                if (loaded != null) _samples.AddRange(loaded);
            }
        }
        catch { /* corrupt history: start fresh */ }
    }

    public void Add(UsageSample sample)
    {
        _samples.Add(sample);
        var cutoff = sample.TimestampUtc - Retention;
        _samples.RemoveAll(s => s.TimestampUtc < cutoff);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_samples));
        }
        catch { /* persistence is best-effort; in-memory data stays valid */ }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add UsageHistory.cs Tests/UsageHistoryTests.cs
git commit -m "Add UsageHistory sample store with 14-day retention"
```

---

### Task 5: Forecast math

**Files:**
- Modify: `UsageHistory.cs` (add `ForecastResult` record + static `Forecast` method)
- Test: `Tests/ForecastTests.cs`

**Interfaces:**
- Consumes: `UsageSample`, `UsageHistory` from Task 4
- Produces: `record ForecastResult(double PercentPerHour, DateTime? LimitAtUtc, bool ReachesBeforeReset)` with `string ToDisplayText()`; `static ForecastResult? UsageHistory.Forecast(IReadOnlyList<UsageSample> samples, Func<UsageSample, double> percent, DateTime windowStartUtc, DateTime? resetsAtUtc, DateTime nowUtc, TimeSpan lookback)` — returns null when data is insufficient (< 3 samples or span < 10 min)

- [ ] **Step 1: Write the failing tests**

`Tests/ForecastTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public class ForecastTests
{
    private static readonly DateTime Now = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Samples every 6 minutes over the last hour, rising at ratePerHour from startPct.</summary>
    private static List<UsageSample> Rising(double startPct, double ratePerHour)
    {
        var list = new List<UsageSample>();
        for (int i = 0; i <= 10; i++)
        {
            var t = Now.AddMinutes(-60 + i * 6);
            var pct = startPct + ratePerHour * (i * 6 / 60.0);
            list.Add(new UsageSample(t, pct, 0, -1));
        }
        return list;
    }

    [Fact]
    public void SteadyBurnPredictsLimitTime()
    {
        // 50% one hour ago, +10%/h → 60% now → 100% in 4h
        var f = UsageHistory.Forecast(Rising(50, 10), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.InRange(f!.PercentPerHour, 9.5, 10.5);
        Assert.NotNull(f.LimitAtUtc);
        Assert.InRange((f.LimitAtUtc!.Value - Now).TotalHours, 3.8, 4.2);
        Assert.True(f.ReachesBeforeReset); // limit in ~4h, reset in 5h
    }

    [Fact]
    public void LimitAfterResetIsNotFlagged()
    {
        var f = UsageHistory.Forecast(Rising(50, 10), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(2), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.False(f!.ReachesBeforeReset); // limit in ~4h, reset already in 2h
        Assert.Equal("ok until reset", f.ToDisplayText());
    }

    [Fact]
    public void FlatUsageHasNoLimitTime()
    {
        var f = UsageHistory.Forecast(Rising(60, 0), s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.Null(f!.LimitAtUtc);
        Assert.False(f.ReachesBeforeReset);
    }

    [Fact]
    public void TooFewSamplesReturnsNull()
    {
        var samples = Rising(50, 10).TakeLast(2).ToList();
        var f = UsageHistory.Forecast(samples, s => s.SessionPercent,
            Now.AddHours(-2), Now.AddHours(5), Now, TimeSpan.FromHours(1));
        Assert.Null(f);
    }

    [Fact]
    public void SamplesBeforeWindowStartAreIgnored()
    {
        // Pre-reset samples at 95% would poison the slope; window started 30 min ago
        var samples = new List<UsageSample>
        {
            new(Now.AddMinutes(-50), 95, 0, -1),
            new(Now.AddMinutes(-45), 96, 0, -1),
        };
        for (int i = 0; i <= 5; i++)
            samples.Add(new(Now.AddMinutes(-30 + i * 6), 2 + i, 0, -1));

        var f = UsageHistory.Forecast(samples, s => s.SessionPercent,
            Now.AddMinutes(-30), Now.AddHours(4), Now, TimeSpan.FromHours(1));
        Assert.NotNull(f);
        Assert.InRange(f!.PercentPerHour, 8, 12); // 1% per 6 min = 10%/h, not distorted by the 95s
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: FAIL to compile (`Forecast` missing).

- [ ] **Step 3: Implement**

Add to `UsageHistory.cs`, above the `UsageHistory` class:

```csharp
/// <summary>Linear burn-rate extrapolation result.</summary>
public sealed record ForecastResult(double PercentPerHour, DateTime? LimitAtUtc, bool ReachesBeforeReset)
{
    public string ToDisplayText()
    {
        if (LimitAtUtc == null || !ReachesBeforeReset) return "ok until reset";
        var local = LimitAtUtc.Value.ToLocalTime();
        return local.Date == DateTime.Now.Date ? $"limit ~{local:HH:mm}" : $"limit {local:ddd HH:mm}";
    }
}
```

Add to the `UsageHistory` class:

```csharp
    /// <summary>
    /// Least-squares slope over the samples inside the current limit window,
    /// capped to a lookback horizon. Returns null when there is not enough data
    /// (fewer than 3 samples or spanning less than 10 minutes).
    /// </summary>
    public static ForecastResult? Forecast(
        IReadOnlyList<UsageSample> samples, Func<UsageSample, double> percent,
        DateTime windowStartUtc, DateTime? resetsAtUtc, DateTime nowUtc, TimeSpan lookback)
    {
        var cutoff = nowUtc - lookback;
        if (windowStartUtc > cutoff) cutoff = windowStartUtc;

        var pts = samples.Where(s => s.TimestampUtc >= cutoff && s.TimestampUtc <= nowUtc)
                         .OrderBy(s => s.TimestampUtc)
                         .ToList();
        if (pts.Count < 3) return null;
        if (pts[^1].TimestampUtc - pts[0].TimestampUtc < TimeSpan.FromMinutes(10)) return null;

        var t0 = pts[0].TimestampUtc;
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in pts)
        {
            double x = (p.TimestampUtc - t0).TotalHours, y = percent(p);
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double denom = n * sxx - sx * sx;
        if (denom <= 0) return null;
        double slope = (n * sxy - sx * sy) / denom;

        if (slope <= 0.01) return new ForecastResult(slope, null, false);

        double current = percent(pts[^1]);
        var limitAt = nowUtc.AddHours((100 - current) / slope);
        bool before = resetsAtUtc.HasValue && limitAt < resetsAtUtc.Value;
        return new ForecastResult(slope, limitAt, before);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add UsageHistory.cs Tests/ForecastTests.cs
git commit -m "Add least-squares burn-rate forecast"
```

---

### Task 6: JsonlUsageReader

**Files:**
- Create: `JsonlUsageReader.cs`
- Test: `Tests/JsonlUsageReaderTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `record ModelUsage(string Model, long InputTokens, long OutputTokens, long CacheCreateTokens, long CacheReadTokens)` with `long TotalTokens`, `decimal CostUsd`; `record LocalUsageReport(IReadOnlyList<ModelUsage> Today, IReadOnlyList<ModelUsage> Week)` with `static LocalUsageReport Empty`; `static class JsonlUsageReader` with `static string DefaultProjectsDir`, `static LocalUsageReport Read(string projectsDir, DateTime todayStartUtc, DateTime weekStartUtc)`, `internal static bool TryParseLine(string line, out JsonlEntry? entry)`; `internal record JsonlEntry(string MessageId, string Model, DateTime TimestampUtc, long InputTokens, long OutputTokens, long CacheCreateTokens, long CacheReadTokens)`

- [ ] **Step 1: Write the failing tests**

`Tests/JsonlUsageReaderTests.cs`:

```csharp
namespace ClaudeUsageMonitor.Tests;

public sealed class JsonlUsageReaderTests : IDisposable
{
    private readonly string _dir;

    public JsonlUsageReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cum-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_dir, "proj-a"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string Line(string id, string model, string timestamp,
        long input = 100, long output = 50, long cacheCreate = 10, long cacheRead = 5) =>
        $$"""{"type":"assistant","timestamp":"{{timestamp}}","message":{"id":"{{id}}","model":"{{model}}","usage":{"input_tokens":{{input}},"output_tokens":{{output}},"cache_creation_input_tokens":{{cacheCreate}},"cache_read_input_tokens":{{cacheRead}}}}}""";

    [Fact]
    public void ParsesRealShapedLine()
    {
        Assert.True(JsonlUsageReader.TryParseLine(
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"), out var e));
        Assert.NotNull(e);
        Assert.Equal("msg_1", e!.MessageId);
        Assert.Equal("claude-sonnet-5", e.Model);
        Assert.Equal(100, e.InputTokens);
        Assert.Equal(5, e.CacheReadTokens);
        Assert.Equal(DateTimeKind.Utc, e.TimestampUtc.Kind);
    }

    [Fact]
    public void RejectsNonAssistantAndSynthetic()
    {
        Assert.False(JsonlUsageReader.TryParseLine("""{"type":"user","message":{"usage":{}}}""", out _));
        Assert.False(JsonlUsageReader.TryParseLine(
            Line("msg_x", "<synthetic>", "2026-07-06T10:00:00.000Z"), out _));
        Assert.False(JsonlUsageReader.TryParseLine("not json", out _));
    }

    [Fact]
    public void AggregatesDedupsAndSplitsTodayVsWeek()
    {
        var file = Path.Combine(_dir, "proj-a", "session1.jsonl");
        File.WriteAllLines(file, new[]
        {
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"),          // today
            Line("msg_1", "claude-sonnet-5", "2026-07-06T10:00:00.000Z"),          // duplicate: ignored
            Line("msg_2", "claude-fable-5",  "2026-07-04T10:00:00.000Z"),          // this week, not today
            Line("msg_3", "claude-sonnet-5", "2026-06-20T10:00:00.000Z"),          // before week: ignored
            """{"type":"summary","summary":"noise line without usage"}""",
        });
        // Entries older than the week must be filtered even though the file is new
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);

        var report = JsonlUsageReader.Read(_dir,
            todayStartUtc: new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            weekStartUtc: new DateTime(2026, 7, 2, 7, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, report.Week.Count);   // sonnet + fable
        Assert.Single(report.Today);          // only msg_1
        var sonnetWeek = report.Week.Single(m => m.Model == "claude-sonnet-5");
        Assert.Equal(100, sonnetWeek.InputTokens); // dedup: counted once
    }

    [Fact]
    public void MissingDirectoryReturnsEmpty()
    {
        var report = JsonlUsageReader.Read(Path.Combine(_dir, "does-not-exist"), DateTime.UtcNow, DateTime.UtcNow);
        Assert.Empty(report.Week);
        Assert.Empty(report.Today);
    }

    [Fact]
    public void CostUsesPerModelPricing()
    {
        // Sonnet: $3/M in + $15/M out → 1M each = $18
        var sonnet = new ModelUsage("claude-sonnet-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(18m, sonnet.CostUsd);

        // Haiku: $1/M in + $5/M out
        var haiku = new ModelUsage("claude-haiku-4-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(6m, haiku.CostUsd);

        // Unknown flagship falls back to opus-tier pricing: $15/M in + $75/M out
        var fable = new ModelUsage("claude-fable-5", 1_000_000, 1_000_000, 0, 0);
        Assert.Equal(90m, fable.CostUsd);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: FAIL to compile.

- [ ] **Step 3: Implement**

`JsonlUsageReader.cs`:

```csharp
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

                // Claude Code appends to these files while running — share everything
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: all pass.

Real-data verification (numbers plausible against actual `~/.claude/projects` logs) happens in Task 9 Step 7 with the popup on screen.

- [ ] **Step 5: Commit**

```bash
git add JsonlUsageReader.cs Tests/JsonlUsageReaderTests.cs
git commit -m "Add JSONL usage aggregation with pricing table"
```

---

### Task 7: PopupForm

**Files:**
- Create: `PopupForm.cs`

**Interfaces:**
- Consumes: `UsageData` (incl. `HasOpus`/`OpusPercent`/`OpusResetText`, `SessionWindowStartUtc`, `WeeklyWindowStartUtc`), `UsageHistory.Samples`, `UsageHistory.Forecast(...)`, `ForecastResult.ToDisplayText()`, `JsonlUsageReader.Read(...)`, `LocalUsageReport`, `ModelUsage`
- Produces: `PopupForm(UsageData data, UsageHistory history)`, `void UpdateData(UsageData data)`. Closes itself on `Deactivate` and Escape.

- [ ] **Step 1: Implement**

`PopupForm.cs`:

```csharp
namespace ClaudeUsageMonitor;

/// <summary>
/// Borderless dark dashboard panel near the tray. Top: account-wide windows
/// (session / weekly / opus / extra) with burn-rate forecast. Bottom: local
/// Claude Code usage aggregated from JSONL logs (async, 5-minute cache).
/// Closes on focus loss or Escape.
/// </summary>
public sealed class PopupForm : Form
{
    private const int W = 380;

    private static readonly Color CBg     = Color.FromArgb(24, 24, 27);
    private static readonly Color COk     = Color.FromArgb(34, 197, 94);
    private static readonly Color CWarn   = Color.FromArgb(251, 191, 36);
    private static readonly Color CCrit   = Color.FromArgb(239, 68, 68);
    private static readonly Color CGray   = Color.FromArgb(140, 140, 150);
    private static readonly Color CAccent = Color.FromArgb(217, 119, 87);

    // JSONL scans are file-system sweeps; cache the result across popup openings
    private static LocalUsageReport? _cachedReport;
    private static DateTime _cacheTimeUtc;

    private readonly UsageHistory _history;
    private UsageData _data;

    public PopupForm(UsageData data, UsageHistory history)
    {
        _data = data;
        _history = history;

        FormBorderStyle = FormBorderStyle.None;
        BackColor = CBg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f);
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;

        Deactivate += (_, _) => Close();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        Rebuild();
    }

    public void UpdateData(UsageData data)
    {
        _data = data;
        Rebuild();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        SuspendLayout();
        while (Controls.Count > 0)
        {
            var c = Controls[0];
            Controls.RemoveAt(0);
            c.Dispose();
        }

        var d = _data;
        int y = 14;

        AddHeader(ref y, "ACCOUNT USAGE (ALL CHANNELS)");

        var sessionForecast = ForecastFor(s => s.SessionPercent, d.SessionWindowStartUtc, d.SessionResetsAt, TimeSpan.FromHours(1));
        AddBar(ref y, "Session (5h)", d.SessionPercent,
            $"Reset: {d.SessionResetText} | {d.SessionPaceText}", ForecastText(sessionForecast));

        if (d.HasWeekly)
        {
            var weeklyForecast = ForecastFor(s => s.WeeklyPercent, d.WeeklyWindowStartUtc, d.WeeklyResetsAt, TimeSpan.FromHours(24));
            AddBar(ref y, "Weekly (7d)", d.WeeklyPercent,
                $"Reset: {d.WeeklyResetText} | {d.WeeklyPaceText}", ForecastText(weeklyForecast));
        }

        if (d.HasOpus)
            AddBar(ref y, "Opus (7d)", d.OpusPercent, $"Reset: {d.OpusResetText}", null);

        if (d.ExtraEnabled)
            AddBar(ref y, "Extra usage", d.ExtraPercent,
                $"${d.ExtraUsedDollars:F2} / ${d.ExtraLimitDollars:F2}", null);

        y += 4;
        AddSeparator(ref y);
        AddHeader(ref y, "CLAUDE CODE (LOCAL LOGS)");

        var todayLbl  = AddInfoLine(ref y, "Today: loading...");
        var weekLbl   = AddInfoLine(ref y, "");
        var modelsLbl = AddInfoLine(ref y, "");

        y += 6;
        AddSeparator(ref y);
        Controls.Add(new Label
        {
            Text = $"Updated {d.FetchedAt:HH:mm:ss}",
            ForeColor = CGray, Font = new Font("Segoe UI", 8f),
            Location = new Point(16, y), AutoSize = true,
        });
        y += 26;

        ClientSize = new Size(W, y);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - W - 12, wa.Bottom - Height - 12);

        ResumeLayout();
        LoadLocalUsage(todayLbl, weekLbl, modelsLbl);
    }

    private ForecastResult? ForecastFor(Func<UsageSample, double> percent,
        DateTime? windowStartUtc, DateTime? resetsAtUtc, TimeSpan lookback) =>
        windowStartUtc is DateTime ws
            ? UsageHistory.Forecast(_history.Samples, percent, ws, resetsAtUtc, DateTime.UtcNow, lookback)
            : null;

    private static string ForecastText(ForecastResult? f) =>
        f == null
            ? "Pace: gathering data"
            : $"Pace: {f.ToDisplayText()}" + (f.PercentPerHour > 0.01 ? $" ({f.PercentPerHour:0.0}%/h)" : "");

    // ── Local usage section ──────────────────────────────────────────────────

    private void LoadLocalUsage(Label todayLbl, Label weekLbl, Label modelsLbl)
    {
        if (_cachedReport != null && DateTime.UtcNow - _cacheTimeUtc < TimeSpan.FromMinutes(5))
        {
            ApplyLocalUsage(_cachedReport, todayLbl, weekLbl, modelsLbl);
            return;
        }

        var weekStart = _data.WeeklyWindowStartUtc ?? DateTime.UtcNow.AddDays(-7);
        var todayStart = DateTime.Today.ToUniversalTime();

        Task.Run(() => JsonlUsageReader.Read(JsonlUsageReader.DefaultProjectsDir, todayStart, weekStart))
            .ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || IsDisposed) return;
                BeginInvoke(() =>
                {
                    _cachedReport = t.Result;
                    _cacheTimeUtc = DateTime.UtcNow;
                    if (!todayLbl.IsDisposed)
                        ApplyLocalUsage(t.Result, todayLbl, weekLbl, modelsLbl);
                });
            });
    }

    private static void ApplyLocalUsage(LocalUsageReport r, Label todayLbl, Label weekLbl, Label modelsLbl)
    {
        if (r.Week.Count == 0)
        {
            todayLbl.Text = "No local Claude Code data found.";
            weekLbl.Text = "";
            modelsLbl.Text = "";
            return;
        }
        todayLbl.Text  = $"Today: {Tok(r.Today.Sum(m => m.TotalTokens))} tokens (~${r.Today.Sum(m => m.CostUsd):0.00} API-equivalent)";
        weekLbl.Text   = $"This week: {Tok(r.Week.Sum(m => m.TotalTokens))} tokens (~${r.Week.Sum(m => m.CostUsd):0.00})";
        modelsLbl.Text = "Models: " + string.Join(", ", r.Week.Take(3).Select(m => $"{ShortModel(m.Model)} ${m.CostUsd:0.00}"));
    }

    private static string Tok(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1_000 ? $"{n / 1_000.0:0}k" : n.ToString();

    private static string ShortModel(string model) => model.Replace("claude-", "");

    // ── Control helpers ──────────────────────────────────────────────────────

    private void AddHeader(ref int y, string text)
    {
        Controls.Add(new Label
        {
            Text = text, ForeColor = CGray,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            Location = new Point(16, y), AutoSize = true,
        });
        y += 22;
    }

    private void AddBar(ref int y, string label, double pct, string sub, string? forecast)
    {
        var color = pct >= 90 ? CCrit : pct >= 75 ? CWarn : COk;

        Controls.Add(new Label
        {
            Text = $"{label}: {pct:0.0}%", ForeColor = color,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Location = new Point(16, y), Size = new Size(W - 32, 20),
        });
        y += 22;

        var bar = new Panel { Location = new Point(16, y), Size = new Size(W - 32, 12), BackColor = Color.FromArgb(45, 45, 50) };
        bar.Paint += (_, e) =>
        {
            var wpx = (int)(bar.Width * Math.Min(pct, 100) / 100);
            if (wpx > 0)
            {
                using var b = new SolidBrush(color);
                e.Graphics.FillRectangle(b, 0, 0, wpx, bar.Height);
            }
        };
        Controls.Add(bar);
        y += 16;

        Controls.Add(new Label
        {
            Text = sub, ForeColor = CGray, Font = new Font("Segoe UI", 8.5f),
            Location = new Point(16, y), Size = new Size(W - 32, 16),
        });
        y += 16;

        if (forecast != null)
        {
            Controls.Add(new Label
            {
                Text = forecast, ForeColor = CAccent, Font = new Font("Segoe UI", 8.5f),
                Location = new Point(16, y), Size = new Size(W - 32, 16),
            });
            y += 16;
        }
        y += 6;
    }

    private void AddSeparator(ref int y)
    {
        Controls.Add(new Panel { Location = new Point(16, y), Size = new Size(W - 32, 1), BackColor = Color.FromArgb(55, 55, 60) });
        y += 10;
    }

    private Label AddInfoLine(ref int y, string text)
    {
        var l = new Label
        {
            Text = text, ForeColor = Color.White, Font = new Font("Segoe UI", 9f),
            Location = new Point(16, y), Size = new Size(W - 32, 18),
        };
        Controls.Add(l);
        y += 20;
        return l;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: succeeds. (The form is not reachable from the UI yet; wiring is Task 9.)

- [ ] **Step 3: Commit**

```bash
git add PopupForm.cs
git commit -m "Add popup dashboard form"
```

---

### Task 8: TaskbarWidget left-click

**Files:**
- Modify: `TaskbarWidget.cs`

**Interfaces:**
- Consumes: existing `WidgetNativeWindow`
- Produces: `public Action? OnLeftClick { get; set; }` on `TaskbarWidget` (invoked on WM_LBUTTONUP inside the widget)

- [ ] **Step 1: Implement**

In `WidgetNativeWindow` (nested class in `TaskbarWidget.cs`), next to `WM_RBUTTONUP`:

```csharp
        private const int WM_LBUTTONUP = 0x0202;
```

Add a property next to `ContextMenu`:

```csharp
        public Action? OnLeftClick { get; set; }
```

In `WidgetNativeWindow.WndProc`, before the existing right-click branch:

```csharp
            if (m.Msg == WM_LBUTTONUP)
                OnLeftClick?.Invoke();
```

On `TaskbarWidget` (outer class), next to the `ContextMenu` property:

```csharp
    public Action? OnLeftClick
    {
        get => _nw.OnLeftClick;
        set => _nw.OnLeftClick = value;
    }
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add TaskbarWidget.cs
git commit -m "Add left-click callback to taskbar widget"
```

---

### Task 9: MainForm wiring

**Files:**
- Modify: `MainForm.cs`

**Interfaces:**
- Consumes: `AppSettings`, `UsageHistory`, `UsageSample`, `ForecastResult`, `PopupForm`, `TaskbarWidget.OnLeftClick`
- Produces: fully wired app. The old details window (`_widget`, `ShowDetails`, `RefreshWidget`, `AddBar`, `CWeekly`, `PaceColor`) is REMOVED; the popup replaces it.

- [ ] **Step 1: Fields and constructor**

Replace the constants/fields block:

```csharp
    private const int MaxBackoffMs = 960_000;  // 16 min cap
    private int PollIntervalMs => _settings.PollIntervalMinutes * 60_000;
```

(delete `private const int PollIntervalMs = 120_000;`). The field `private int _backoffMs = PollIntervalMs;` must become `private int _backoffMs;` (a field initializer cannot reference the instance property, CS0236); it is set in the constructor below. Replace the field `private Form? _widget;` with:

```csharp
    private readonly AppSettings _settings;
    private readonly UsageHistory _history;
    private PopupForm? _popup;
    private DateTime _popupClosedAtUtc;

    // Notification state: once per threshold per window instance
    private DateTime? _sessNotifyWindow; private int _sessNotifiedLevel;
    private DateTime? _weekNotifyWindow; private int _weekNotifiedLevel;
```

Delete the now-unused `CWeekly` color and the `PaceColor` helper (only the old details window used them).

In the constructor, FIRST lines of the body (before `_fetcher = ...`):

```csharp
        _settings = AppSettings.Load();
        _history = new UsageHistory(Path.Combine(AppSettings.SettingsDir, "history.json"));
        _backoffMs = PollIntervalMs;
```

Change the tray icon wiring: remove `_trayIcon.DoubleClick += (_, _) => ShowDetails();` and add instead:

```csharp
        _trayIcon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };
```

Change the poll timer creation to use the settings-driven interval:

```csharp
        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
```

In the startup one-shot timer: REMOVE the `ShowDetails();` call (no focus-stealing window at boot) and wire the widget click after creating it:

```csharp
            _taskbarWidget = new TaskbarWidget(_lastData);
            _taskbarWidget.ContextMenu = _trayIcon.ContextMenuStrip;
            _taskbarWidget.OnLeftClick = TogglePopup;
```

- [ ] **Step 2: PollAsync success path**

Replace the block after `_lastData = data;` (through `_taskbarWidget?.Update(data);`) with:

```csharp
            _lastData = data;
            _errors = 0;
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;

            _history.Add(new UsageSample(DateTime.UtcNow, data.SessionPercent,
                data.HasWeekly ? data.WeeklyPercent : -1,
                data.HasOpus ? data.OpusPercent : -1));
            CheckNotifications(data);

            var pct = data.SessionPercent;
            var color = pct >= _settings.CritThresholdPercent ? CCrit
                      : pct >= _settings.WarnThresholdPercent ? CWarn : COk;
            var tooltip = data.TooltipText;
            var forecast = SessionForecast(data);
            if (forecast != null) tooltip += $"\nPace: {forecast.ToDisplayText()}";
            SetIcon($"{pct:0}%", color, tooltip);
            _taskbarWidget?.Update(data);
            _popup?.UpdateData(data);
```

(The old `RefreshWidget();` call disappears with the old details window.)

- [ ] **Step 3: New helpers (popup, forecast, notifications)**

Replace the entire old WIDGET section (`ShowDetails`, `RefreshWidget`, `AddBar` and the `_widget` handling inside them) with:

```csharp
    // ═══════════════════════════════════════
    // POPUP
    // ═══════════════════════════════════════

    private void TogglePopup()
    {
        if (_popup != null && !_popup.IsDisposed) { _popup.Close(); _popup = null; return; }
        // Clicking the tray while the popup is open fires Deactivate (popup closes)
        // before MouseUp arrives — without this guard the click would reopen it.
        if ((DateTime.UtcNow - _popupClosedAtUtc).TotalMilliseconds < 300) return;
        if (_lastData == null) return;

        _popup = new PopupForm(_lastData, _history);
        _popup.FormClosed += (_, _) => { _popupClosedAtUtc = DateTime.UtcNow; _popup = null; };
        _popup.Show();
        _popup.Activate();
    }

    private ForecastResult? SessionForecast(UsageData d) =>
        d.SessionWindowStartUtc is DateTime ws
            ? UsageHistory.Forecast(_history.Samples, s => s.SessionPercent, ws,
                d.SessionResetsAt, DateTime.UtcNow, TimeSpan.FromHours(1))
            : null;

    // ═══════════════════════════════════════
    // NOTIFICATIONS
    // ═══════════════════════════════════════

    private void CheckNotifications(UsageData d)
    {
        if (!_settings.NotificationsEnabled) return;
        CheckWindow(ref _sessNotifyWindow, ref _sessNotifiedLevel, d.SessionResetsAt, d.SessionPercent, "Session (5h)");
        if (d.HasWeekly)
            CheckWindow(ref _weekNotifyWindow, ref _weekNotifiedLevel, d.WeeklyResetsAt, d.WeeklyPercent, "Weekly (7d)");
    }

    private void CheckWindow(ref DateTime? window, ref int notifiedLevel,
        DateTime? resetsAt, double pct, string label)
    {
        if (resetsAt != window) { window = resetsAt; notifiedLevel = 0; }

        int level = pct >= _settings.CritThresholdPercent ? 2
                  : pct >= _settings.WarnThresholdPercent ? 1 : 0;
        if (level <= notifiedLevel) return;

        notifiedLevel = level;
        _trayIcon.ShowBalloonTip(6000, "Claude Usage Monitor",
            $"{label}: {pct:0}% used", level == 2 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }
```

Keep `ShowAbout()` unchanged.

- [ ] **Step 4: Menu**

Replace `BuildMenu()` with:

```csharp
    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Details") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        show.Click += (_, _) => TogglePopup();
        m.Items.Add(show);

        m.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => FireAndForget(PollAsync);
        m.Items.Add(refresh);

        var raw = new ToolStripMenuItem("Copy Status Text");
        raw.Click += (_, _) =>
        {
            if (_lastData?.TooltipText != null)
                Clipboard.SetText(_lastData.TooltipText);
        };
        m.Items.Add(raw);

        m.Items.Add(new ToolStripSeparator());

        var interval = new ToolStripMenuItem("Poll interval");
        foreach (var min in new[] { 1, 2, 5, 15 })
        {
            var item = new ToolStripMenuItem($"{min} min") { Checked = _settings.PollIntervalMinutes == min, Tag = min };
            item.Click += (sender, _) =>
            {
                _settings.PollIntervalMinutes = (int)((ToolStripMenuItem)sender!).Tag!;
                _settings.Save();
                _backoffMs = PollIntervalMs;
                _pollTimer.Interval = PollIntervalMs;
                foreach (ToolStripMenuItem it in interval.DropDownItems)
                    it.Checked = (int)it.Tag! == _settings.PollIntervalMinutes;
            };
            interval.DropDownItems.Add(item);
        }
        m.Items.Add(interval);

        var notify = new ToolStripMenuItem("Notifications") { Checked = _settings.NotificationsEnabled, CheckOnClick = true };
        notify.CheckedChanged += (_, _) => { _settings.NotificationsEnabled = notify.Checked; _settings.Save(); };
        m.Items.Add(notify);

        var autostart = new ToolStripMenuItem("Start with Windows") { Checked = AppSettings.IsAutostartEnabled(), CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => AppSettings.SetAutostart(autostart.Checked);
        m.Items.Add(autostart);

        m.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem("About");
        about.Click += (_, _) => ShowAbout();
        m.Items.Add(about);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _cts.Cancel(); _trayIcon.Visible = false; Application.Exit(); };
        m.Items.Add(exit);

        return m;
    }
```

- [ ] **Step 5: Dispose**

In `Dispose(bool)`, replace `_widget?.Dispose();` with `_popup?.Dispose();`.

- [ ] **Step 6: Build and run tests**

Run: `dotnet build -c Release && dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: build succeeds, all tests pass. Compiler must report no unused-member warnings for removed helpers (if `PaceColor`/`CWeekly` remain referenced anywhere, that is a leftover — remove the reference).

- [ ] **Step 7: Manual verification**

Run: `dotnet run -c Release`
Check:
1. Tray icon appears with percent; NO details window auto-opens at startup.
2. Left-click tray icon → popup opens bottom-right; shows session/weekly bars, reset lines, "Pace: gathering data" (first run) or a forecast; local section fills within ~2 s with real token numbers.
3. Click elsewhere → popup closes. Left-click again → reopens (no flicker-reopen when clicking the tray icon while open).
4. Left-click the taskbar widget → same popup.
5. Context menu: Poll interval shows a checkmark on the current value; toggling Notifications and Start with Windows persists (check `%APPDATA%\ClaudeUsageMonitor\settings.json` and `HKCU\...\Run`).
6. `%APPDATA%\ClaudeUsageMonitor\history.json` exists and grows by one sample per poll.

- [ ] **Step 8: Commit**

```bash
git add MainForm.cs
git commit -m "Wire popup, history sampling, notifications and settings menu; remove old details window"
```

---

### Task 10: Version, README, final verification

**Files:**
- Modify: `ClaudeUsageMonitor.csproj` (version 0.5.0)
- Modify: `README.md` (feature list: popup dashboard, forecast, local Claude Code usage, notifications, settings)

**Interfaces:**
- Consumes: everything
- Produces: release-ready 0.5.0

- [ ] **Step 1: Bump version**

In `ClaudeUsageMonitor.csproj`: `<Version>0.5.0</Version>`.

- [ ] **Step 2: Update README**

Update the feature list to include: popup dashboard (left-click tray or widget), burn-rate forecast per limit window, local Claude Code token/cost breakdown from JSONL logs, threshold notifications, poll interval / notifications / autostart settings. Mention: claude.ai web usage is only visible as part of the account-wide percentages; token detail exists only for Claude Code. Keep the existing document structure and tone.

- [ ] **Step 3: Full verification**

Run: `dotnet build -c Release && dotnet test Tests/ClaudeUsageMonitor.Tests.csproj`
Expected: clean build, all tests pass.

Run: `dotnet run -c Release` and repeat the Task 9 Step 7 checklist once. Additionally: let it run past one poll interval and confirm the popup forecast line switches from "gathering data" to a real forecast after ≥ 3 polls in ≥ 10 minutes.

- [ ] **Step 4: Commit**

```bash
git add ClaudeUsageMonitor.csproj README.md
git commit -m "Add usage dashboard with forecast and local stats; bump version to 0.5.0"
```
