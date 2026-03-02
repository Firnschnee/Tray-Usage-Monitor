using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>
/// Tray app. Reads OAuth token from Claude Code, fetches usage, displays icon.
/// </summary>
public sealed class MainForm : Form
{
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly UsageFetcher _fetcher;

    private UsageData? _lastData;
    private bool _polling;
    private int _errors;
    private bool _tokenWarningShown;

    private Form? _widget;
    private System.Windows.Forms.Timer? _widgetTimer;
    private TaskbarWidget? _taskbarWidget;

    private static readonly Color COk = Color.FromArgb(34, 197, 94);
    private static readonly Color CWarn = Color.FromArgb(251, 191, 36);
    private static readonly Color CCrit = Color.FromArgb(239, 68, 68);
    private static readonly Color CGray = Color.FromArgb(156, 163, 175);
    private static readonly Color CWeekly = Color.FromArgb(56, 189, 248); // cyan — weekly reference on session bar

    // over-pace = red (burning quota fast), under-pace = green (headroom), on-pace = yellow
    private static Color PaceColor(double diff) =>
        diff >= 5 ? CCrit : diff <= -5 ? COk : CWarn;

    public MainForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = Size.Empty;

        _fetcher = new UsageFetcher();

        _trayIcon = new NotifyIcon
        {
            Icon = MakeIcon("...", CGray),
            Text = "Claude Usage Monitor",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowDetails();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 120_000 }; // 2 min
        _pollTimer.Tick += async (_, _) => await PollAsync();

        // Load event won't fire because SetVisibleCore(false) prevents visibility.
        // Use a one-shot timer to kick off initial work once the message loop is running.
        var startup = new System.Windows.Forms.Timer { Interval = 200 };
        startup.Tick += async (_, _) =>
        {
            startup.Stop();
            startup.Dispose();
            await PollAsync();
            _pollTimer.Start();
            _taskbarWidget = new TaskbarWidget(_lastData);
            ShowDetails();
        };
        startup.Start();
    }

    // ═══════════════════════════════════════
    // POLLING
    // ═══════════════════════════════════════

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;

        try
        {
            var token = CredentialReader.GetAccessToken();
            if (token == null)
            {
                // Diagnostik: Warum kein Token?
                var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "?";
                var credFile = Path.Combine(userProfile, ".claude", ".credentials.json");
                var fileExists = File.Exists(credFile);
                var diagMsg = $"No OAuth token found.\n" +
                              $"File: {credFile}\n" +
                              $"Exists: {fileExists}\n" +
                              $"Please run 'claude login'.";

                SetIcon("!", CCrit, diagMsg);
                if (!_tokenWarningShown)
                {
                    _tokenWarningShown = true;
                    _trayIcon.ShowBalloonTip(10000, "Claude Usage Monitor", diagMsg, ToolTipIcon.Warning);
                }
                return;
            }

            _tokenWarningShown = false;
            var data = await _fetcher.FetchAsync(token);
            _lastData = data;
            _errors = 0;

            var pct = data.SessionPercent;
            var color = pct >= 90 ? CCrit : pct >= 75 ? CWarn : COk;
            SetIcon($"{pct:0}%", color, data.TooltipText);
            RefreshWidget();
            _taskbarWidget?.Update(data);
        }
        catch (UnauthorizedAccessException)
        {
            SetIcon("AUTH", CCrit, "OAuth token expired.\nRun 'claude login'.");
            _trayIcon.ShowBalloonTip(8000, "Token expired",
                "Please run 'claude login' in the terminal.", ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _errors++;
            SetIcon("ERR", CCrit, $"Error: {ex.Message}");
            if (_errors >= 3)
                _trayIcon.ShowBalloonTip(5000, "Error", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _polling = false;
        }
    }

    // ═══════════════════════════════════════
    // MENU
    // ═══════════════════════════════════════

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Details") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        show.Click += (_, _) => ShowDetails();
        m.Items.Add(show);

        m.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += async (_, _) => await PollAsync();
        m.Items.Add(refresh);

        var raw = new ToolStripMenuItem("Copy Raw JSON");
        raw.Click += (_, _) =>
        {
            if (_lastData?.TooltipText != null)
            {
                // Wir kopieren die letzte Raw-Response falls vorhanden
                Clipboard.SetText(_lastData.TooltipText);
            }
        };
        m.Items.Add(raw);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _trayIcon.Visible = false; Application.Exit(); };
        m.Items.Add(exit);

        return m;
    }

    // ═══════════════════════════════════════
    // WIDGET
    // ═══════════════════════════════════════

    private void ShowDetails()
    {
        if (_widget != null && !_widget.IsDisposed)
        {
            _widget.Activate();
            _ = PollAsync();
            return;
        }

        _widget = new Form
        {
            Text = "Claude Usage",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 60),
        };

        // Position near system tray (bottom-right)
        var screen = Screen.PrimaryScreen!.WorkingArea;
        _widget.StartPosition = FormStartPosition.Manual;
        _widget.Location = new Point(screen.Right - 430, screen.Bottom - 120);

        _widget.FormClosed += (_, _) =>
        {
            _widgetTimer?.Stop();
            _widgetTimer?.Dispose();
            _widgetTimer = null;
            _widget = null;
        };

        if (_lastData != null)
            RefreshWidget();
        else
            _widget.Controls.Add(new Label
            {
                Text = "Loading...",
                Location = new Point(20, 15), Size = new Size(370, 22),
                ForeColor = CGray, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            });

        _widgetTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _widgetTimer.Tick += async (_, _) => await PollAsync();
        _widgetTimer.Start();

        _widget.Show();

        if (_lastData == null)
            _ = PollAsync();
    }

    private void RefreshWidget()
    {
        if (_widget == null || _widget.IsDisposed || _lastData == null) return;

        _widget.SuspendLayout();
        _widget.Controls.Clear();

        var d = _lastData;
        var updated = $" · {d.FetchedAt:HH:mm:ss}";

        int y = 15;
        // Session bar: colored session-pace marker + cyan weekly-reference marker (when available)
        var sessionSub = $"Reset: {d.SessionResetText} | {d.SessionPaceText}";
        if (d.HasWeekly)
            AddBar(_widget, ref y, "Session (5h)", d.SessionPercent, sessionSub,
                (d.SessionExpectedPercent, PaceColor(d.SessionPaceDiff)),
                (d.WeeklyExpectedPercent, CWeekly));
        else
            AddBar(_widget, ref y, "Session (5h)", d.SessionPercent, sessionSub + updated,
                (d.SessionExpectedPercent, PaceColor(d.SessionPaceDiff)));

        // Weekly bar: colored marker + pace status + updated time in subtitle
        if (d.HasWeekly)
        {
            var paceStatus = d.WeeklyPaceDiff >= 5 ? "ahead" : d.WeeklyPaceDiff <= -5 ? "under" : "on pace";
            var weeklySub = $"Reset: {d.WeeklyResetText} | {d.WeeklyPaceDiff:+0.0;-0.0;0.0}% {paceStatus}";
            AddBar(_widget, ref y, "Weekly (7d)", d.WeeklyPercent, weeklySub + updated,
                (d.WeeklyExpectedPercent, PaceColor(d.WeeklyPaceDiff)));
        }

        if (d.ExtraEnabled) AddBar(_widget, ref y, "Extra Usage", d.ExtraPercent,
            $"${d.ExtraUsedDollars:F2} / ${d.ExtraLimitDollars:F2}" + updated);

        // Size the widget exactly to content with a small bottom margin
        _widget.ClientSize = new Size(400, y + 8);

        _widget.ResumeLayout();
    }

    private static void AddBar(Form f, ref int y, string label, double pct, string sub,
        params (double Pct, Color Clr)[] markers)
    {
        var color = pct >= 90 ? CCrit : pct >= 75 ? CWarn : COk;

        f.Controls.Add(new Label
        {
            Text = $"{label}: {pct:0.0}%",
            Location = new Point(20, y), Size = new Size(370, 22),
            ForeColor = color, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        });
        y += 24;

        var bar = new Panel { Location = new Point(20, y), Size = new Size(370, 14), BackColor = Color.FromArgb(45, 45, 50) };
        bar.Paint += (_, e) =>
        {
            var w = (int)(bar.Width * Math.Min(pct, 100) / 100);
            if (w > 0) { using var b = new SolidBrush(color); e.Graphics.FillRectangle(b, 0, 0, w, bar.Height); }

            // Draw each pace marker as a colored vertical line
            foreach (var (mPct, mClr) in markers)
            {
                if (mPct < 0) continue;
                var mx = (int)(bar.Width * Math.Min(mPct, 100) / 100);
                using var pen = new Pen(Color.FromArgb(210, mClr), 2);
                e.Graphics.DrawLine(pen, mx, 0, mx, bar.Height);
            }
        };
        f.Controls.Add(bar);
        y += 18;

        f.Controls.Add(new Label
        {
            Text = sub, Location = new Point(20, y), Size = new Size(370, 18),
            ForeColor = Color.FromArgb(140, 140, 150), Font = new Font("Segoe UI", 8.5f),
        });
        y += 18;
    }

    // ═══════════════════════════════════════
    // ICON RENDERING
    // ═══════════════════════════════════════

    private static Icon MakeIcon(string text, Color color)
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(30, 30, 30));
        var r = new Rectangle(0, 0, sz, sz);
        using var rr = new GraphicsPath();
        rr.AddArc(r.X, r.Y, 8, 8, 180, 90);
        rr.AddArc(r.Right - 8, r.Y, 8, 8, 270, 90);
        rr.AddArc(r.Right - 8, r.Bottom - 8, 8, 8, 0, 90);
        rr.AddArc(r.X, r.Bottom - 8, 8, 8, 90, 90);
        rr.CloseFigure();
        g.FillPath(bg, rr);

        using var font = new Font("Segoe UI", text.Length > 3 ? 7f : 9f, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0, sz, sz), fmt);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private void SetIcon(string text, Color color, string tooltip)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIcon(text, color, tooltip)); return; }
        var old = _trayIcon.Icon;
        _trayIcon.Icon = MakeIcon(text, color);
        _trayIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        old?.Dispose();
    }

    // ═══════════════════════════════════════
    // LIFECYCLE
    // ═══════════════════════════════════════

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _widgetTimer?.Dispose(); _widget?.Dispose(); _taskbarWidget?.Dispose(); _pollTimer?.Dispose(); _trayIcon?.Dispose(); _fetcher?.Dispose(); }
        base.Dispose(disposing);
    }
}
