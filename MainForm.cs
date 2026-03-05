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
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private readonly CancellationTokenSource _cts = new();

    private TaskbarWidget? _taskbarWidget;

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
        _trayIcon.DoubleClick += async (_, _) => await PollAsync();

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
                System.Diagnostics.Debug.WriteLine($"[Poll] Credentials not found. File: {credFile}, Exists: {File.Exists(credFile)}");
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

            var pct = data.SessionPercent;
            var color = pct >= 90 ? CCrit : pct >= 75 ? CWarn : COk;
            SetIcon($"{pct:0}%", color, data.TooltipText);
            _taskbarWidget?.Update(data);
        }
        catch (OperationCanceledException) { }
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

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += async (_, _) => await PollAsync();
        m.Items.Add(refresh);

        var raw = new ToolStripMenuItem("Copy Status Text");
        raw.Click += (_, _) =>
        {
            if (_lastData?.TooltipText != null)
                Clipboard.SetText(_lastData.TooltipText);
        };
        m.Items.Add(raw);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _trayIcon.Visible = false; Application.Exit(); };
        m.Items.Add(exit);

        return m;
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
        _trayIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
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
        if (disposing) { _cts.Cancel(); _cts.Dispose(); _taskbarWidget?.Dispose(); _pollTimer?.Dispose(); _trayIcon?.Dispose(); _fetcher?.Dispose(); if (_trayIconHandle != IntPtr.Zero) Win32Interop.DestroyIcon(_trayIconHandle); }
        base.Dispose(disposing);
    }
}
