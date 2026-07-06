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

    private const int MaxBackoffMs = 960_000;  // 16 min cap
    private int PollIntervalMs => _settings.PollIntervalMinutes * 60_000;

    private readonly SemaphoreSlim _pollGuard = new(1, 1);
    private UsageData? _lastData;
    private int _errors;
    private int _backoffMs;
    private bool _tokenWarningShown;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private readonly CancellationTokenSource _cts = new();

    private readonly AppSettings _settings;
    private readonly UsageHistory _history;
    private PopupForm? _popup;
    private DateTime _popupClosedAtUtc;

    // Notification state: once per threshold per window instance
    private DateTime? _sessNotifyWindow; private int _sessNotifiedLevel;
    private DateTime? _weekNotifyWindow; private int _weekNotifiedLevel;

    private TaskbarWidget? _taskbarWidget;

    // Registered once per process; non-zero on success (range 0xC000–0xFFFF)
    private static readonly uint _taskbarCreatedMsg =
        Win32Interop.RegisterWindowMessage("TaskbarCreated");

    private const int WM_POWERBROADCAST     = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;

    private static readonly Color COk = Color.FromArgb(34, 197, 94);
    private static readonly Color CWarn = Color.FromArgb(251, 191, 36);
    private static readonly Color CCrit = Color.FromArgb(239, 68, 68);
    private static readonly Color CGray = Color.FromArgb(156, 163, 175);

    public MainForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = Size.Empty;

        _settings = AppSettings.Load();
        _history = new UsageHistory(Path.Combine(AppSettings.SettingsDir, "history.json"));
        _backoffMs = PollIntervalMs;

        _fetcher = new UsageFetcher();

        var (initIcon, initHandle) = MakeIcon("...", CGray);
        _trayIconHandle = initHandle;
        _trayIcon = new NotifyIcon
        {
            Icon = initIcon,
            Text = "Claude Usage Monitor",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _trayIcon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TogglePopup(); };

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => FireAndForget(PollAsync);

        // Load event won't fire because SetVisibleCore(false) prevents visibility.
        // Use a one-shot timer to kick off initial work once the message loop is running.
        var startup = new System.Windows.Forms.Timer { Interval = 200 };
        startup.Tick += (_, _) => FireAndForget(async () =>
        {
            startup.Stop();
            startup.Dispose();
            await PollAsync();
            _pollTimer.Start();
            _taskbarWidget = new TaskbarWidget(_lastData);
            _taskbarWidget.ContextMenu = _trayIcon.ContextMenuStrip;
            _taskbarWidget.OnLeftClick = TogglePopup;
        });
        startup.Start();
    }

    // ═══════════════════════════════════════
    // POLLING
    // ═══════════════════════════════════════

    private async Task PollAsync()
    {
        if (!_pollGuard.Wait(0)) return;

        try
        {
            var token = CredentialReader.GetAccessToken();
            if (token == null)
            {
                // Diagnostik: Warum kein Token?
                var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "?";
                var credFile = Path.Combine(userProfile, ".claude", ".credentials.json");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Poll] Credentials not found. File: {credFile}, Exists: {File.Exists(credFile)}");
#endif
                var diagMsg = "No OAuth token found.\nPlease run 'claude login'.";

                SetIcon("!", CCrit, diagMsg);
                if (!_tokenWarningShown)
                {
                    _tokenWarningShown = true;
                    _trayIcon.ShowBalloonTip(10000, "Claude Usage Monitor", diagMsg, ToolTipIcon.Warning);
                }
                return;
            }

            _tokenWarningShown = false;
            var data = await _fetcher.FetchAsync(token, _cts.Token);
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
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            _pollTimer.Stop(); // no point retrying until user re-auths
            SetIcon("AUTH", CCrit, "OAuth token expired.\nRun 'claude login'.");
            _trayIcon.ShowBalloonTip(8000, "Token expired",
                "Please run 'claude login' in the terminal.", ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _errors++;
            _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
            _pollTimer.Interval = _backoffMs;
            SetIcon("ERR", CCrit, $"Error: {ex.Message}");
            if (_errors >= 3)
                _trayIcon.ShowBalloonTip(5000, "Error", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    // ═══════════════════════════════════════
    // MENU
    // ═══════════════════════════════════════

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

    // ═══════════════════════════════════════
    // POPUP
    // ═══════════════════════════════════════

    private void TogglePopup()
    {
        if (_popup != null && !_popup.IsDisposed) { _popup.Close(); _popup = null; return; }
        // Clicking the tray while the popup is open fires Deactivate (popup closes)
        // before MouseUp arrives, without this guard the click would reopen it.
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

    private static void ShowAbout()
    {
        var version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version;
        var verStr = version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";

        var dlg = new Form
        {
            Text = "About Claude Usage Monitor",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(300, 110),
            StartPosition = FormStartPosition.CenterScreen,
        };

        dlg.Controls.Add(new Label
        {
            Text = "Claude Usage Monitor",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 18),
            AutoSize = true,
        });

        dlg.Controls.Add(new Label
        {
            Text = $"Version {verStr}",
            ForeColor = Color.FromArgb(140, 140, 150),
            Location = new Point(20, 44),
            AutoSize = true,
        });

        var link = new LinkLabel
        {
            Text = "github.com/Firnschnee/Tray-Usage-Monitor",
            Location = new Point(20, 68),
            AutoSize = true,
            BackColor = Color.Transparent,
            LinkColor = Color.FromArgb(56, 189, 248),
            ActiveLinkColor = Color.White,
        };
        link.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/Firnschnee/Tray-Usage-Monitor",
                UseShellExecute = true,
            });
        dlg.Controls.Add(link);

        dlg.ShowDialog();
    }

    // ═══════════════════════════════════════
    // ASYNC HELPER
    // ═══════════════════════════════════════

    private static string TruncateTooltip(string text)
    {
        if (text.Length <= 127) return text;
        var cut = text.LastIndexOf('\n', 126);
        return cut > 0 ? text[..cut] : text[..127];
    }

    private static async void FireAndForget(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Unhandled] {ex}"); }
    }

    // ═══════════════════════════════════════
    // ICON RENDERING
    // ═══════════════════════════════════════

    private static (Icon icon, IntPtr hicon) MakeIcon(string text, Color color)
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

        var hicon = bmp.GetHicon();
        return (Icon.FromHandle(hicon), hicon);
    }

    private void SetIcon(string text, Color color, string tooltip)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIcon(text, color, tooltip)); return; }
        var old = _trayIcon.Icon;
        var oldHandle = _trayIconHandle;
        var (newIcon, newHandle) = MakeIcon(text, color);
        _trayIcon.Icon = newIcon;
        _trayIconHandle = newHandle;
        _trayIcon.Text = TruncateTooltip(tooltip);
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
    }

    // ═══════════════════════════════════════
    // LIFECYCLE
    // ═══════════════════════════════════════

    protected override void WndProc(ref Message m)
    {
        // Re-embed the taskbar widget whenever Explorer restarts
        if (_taskbarCreatedMsg != 0 && m.Msg == (int)_taskbarCreatedMsg)
            _taskbarWidget?.Reattach();
        // Reposition + re-poll after wake from standby (timer doesn't count sleep time)
        if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_APMRESUMEAUTOMATIC)
        {
            _taskbarWidget?.Reposition();
            // The taskbar may not have finished re-laying out at this point.
            // Schedule a second reposition after 2 s so the widget doesn't
            // sit on top of the "show hidden icons" chevron once the taskbar settles.
            var retryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            retryTimer.Tick += (_, _) =>
            {
                retryTimer.Stop();
                retryTimer.Dispose();
                _taskbarWidget?.Reposition();
            };
            retryTimer.Start();
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;
            FireAndForget(PollAsync);
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts.Cancel(); _cts.Dispose(); _pollGuard.Dispose(); _popup?.Dispose(); _taskbarWidget?.Dispose(); _pollTimer?.Dispose(); _trayIcon?.Dispose(); _fetcher?.Dispose(); if (_trayIconHandle != IntPtr.Zero) Win32Interop.DestroyIcon(_trayIconHandle); }
        base.Dispose(disposing);
    }
}
