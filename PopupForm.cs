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
