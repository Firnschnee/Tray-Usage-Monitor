using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>
/// Hauptform: Lebt ausschließlich im System Tray.
/// Polling, Icon-Rendering, Context-Menu, Dialog-Management.
/// </summary>
public sealed class MainForm : Form
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly ClaudeApiClient _apiClient;
    private AppSettings _settings;

    private UsageData? _lastData;
    private bool _isPolling;
    private bool _authErrorNotified;
    private int _errorCount;

    private static readonly Color ColorOk = Color.FromArgb(34, 197, 94);
    private static readonly Color ColorWarn = Color.FromArgb(251, 191, 36);
    private static readonly Color ColorCrit = Color.FromArgb(239, 68, 68);
    private static readonly Color ColorGray = Color.FromArgb(156, 163, 175);

    public MainForm()
    {
        // Unsichtbare Form
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = Size.Empty;

        _settings = AppSettings.Load();
        _apiClient = new ClaudeApiClient();
        _contextMenu = BuildContextMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = RenderIcon("...", ColorGray),
            Text = "Claude Usage Monitor\nStarting...",
            ContextMenuStrip = _contextMenu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowDetails();

        _pollTimer = new System.Windows.Forms.Timer
        {
            Interval = _settings.PollIntervalSeconds * 1000,
        };
        _pollTimer.Tick += async (_, _) => await PollAsync();

        Load += async (_, _) => await StartupAsync();
    }

    // ══════════════════════════════════════════════
    // STARTUP
    // ══════════════════════════════════════════════

    private async Task StartupAsync()
    {
        var sessionKey = AppSettings.LoadSessionKey();

        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            // Erster Start oder keine gespeicherte Session
            // Versuche erst Silent Login (existierende Browser-Session)
            if (!await TrySilentLoginAsync())
            {
                ShowLoginWindow();
                return;
            }
        }
        else
        {
            _apiClient.SetSessionKey(sessionKey);
        }

        await PollAsync();
        _pollTimer.Start();
    }

    /// <summary>
    /// Versucht einen Silent Login: Öffnet ein unsichtbares WebView2,
    /// das claude.ai lädt. Wenn der User eine existierende OAuth-Session hat
    /// (z.B. Google), wird er automatisch eingeloggt.
    /// </summary>
    private async Task<bool> TrySilentLoginAsync()
    {
        try
        {
            UpdateIcon("...", ColorGray, "Auto-Login...");
            using var login = new LoginForm(silent: true);
            var result = login.ShowDialog();

            if (result == DialogResult.OK && !string.IsNullOrEmpty(login.SessionKey))
            {
                _apiClient.SetSessionKey(login.SessionKey);
                _authErrorNotified = false;
                return true;
            }
        }
        catch { }
        return false;
    }

    private void ShowLoginWindow()
    {
        _pollTimer.Stop();
        using var login = new LoginForm(silent: false);
        var result = login.ShowDialog();

        if (result == DialogResult.OK && !string.IsNullOrEmpty(login.SessionKey))
        {
            _apiClient.SetSessionKey(login.SessionKey);
            _authErrorNotified = false;
            _errorCount = 0;
            _ = PollAsync();
            _pollTimer.Start();
        }
        else if (result == DialogResult.Abort)
        {
            // WebView2 nicht verfügbar → Fallback-Info
            UpdateIcon("!", ColorCrit,
                "WebView2 Runtime benötigt.\nBitte installieren und App neu starten.");
        }
        else
        {
            UpdateIcon("???", ColorGray,
                "Claude Usage Monitor\nKein Login. Rechtsklick → Login");
        }
    }

    // ══════════════════════════════════════════════
    // POLLING
    // ══════════════════════════════════════════════

    private async Task PollAsync()
    {
        if (_isPolling) return;
        _isPolling = true;

        try
        {
            var data = await _apiClient.FetchUsageAsync();
            _lastData = data;
            _errorCount = 0;
            _authErrorNotified = false;

            var pct = data.SessionPercent;
            var color = pct >= 90 ? ColorCrit : pct >= _settings.WarnAtPercent ? ColorWarn : ColorOk;
            UpdateIcon($"{pct:0}%", color, data.TooltipText);
        }
        catch (AuthenticationException)
        {
            _pollTimer.Stop();
            UpdateIcon("AUTH", ColorCrit, "Session abgelaufen!");

            if (!_authErrorNotified)
            {
                _authErrorNotified = true;

                // Erst Silent Re-Login versuchen
                if (await TrySilentLoginAsync())
                {
                    await PollAsync();
                    _pollTimer.Start();
                }
                else
                {
                    _trayIcon.ShowBalloonTip(8000, "Claude.ai Session abgelaufen",
                        "Rechtsklick → Login um dich erneut anzumelden.", ToolTipIcon.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            _errorCount++;
            UpdateIcon("ERR", ColorCrit, $"Fehler: {ex.Message}");

            if (_errorCount >= 5)
            {
                _trayIcon.ShowBalloonTip(5000, "Claude Usage Monitor",
                    $"{_errorCount} Fehler hintereinander.\n{ex.Message}", ToolTipIcon.Error);
            }
        }
        finally
        {
            _isPolling = false;
        }
    }

    // ══════════════════════════════════════════════
    // CONTEXT MENU
    // ══════════════════════════════════════════════

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var show = new ToolStripMenuItem("📊  Show Details") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        show.Click += (_, _) => ShowDetails();
        menu.Items.Add(show);

        menu.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("🔄  Refresh Now");
        refresh.Click += async (_, _) => await PollAsync();
        menu.Items.Add(refresh);

        var login = new ToolStripMenuItem("🔑  Login");
        login.Click += (_, _) => ShowLoginWindow();
        menu.Items.Add(login);

        menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("⚙️  Settings");
        settings.Click += (_, _) => ShowSettingsDialog();
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("❌  Exit");
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        return menu;
    }

    // ══════════════════════════════════════════════
    // TRAY ICON RENDERING
    // ══════════════════════════════════════════════

    private static Icon RenderIcon(string text, Color color)
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // Hintergrund
        using var bg = new SolidBrush(Color.FromArgb(30, 30, 30));
        using var path = RoundedRect(new Rectangle(0, 0, sz, sz), 4);
        g.FillPath(bg, path);

        // Text
        var fontSize = text.Length > 3 ? 7f : 9f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0, sz, sz), fmt);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void UpdateIcon(string text, Color color, string tooltip)
    {
        if (InvokeRequired) { BeginInvoke(() => UpdateIcon(text, color, tooltip)); return; }
        var old = _trayIcon.Icon;
        _trayIcon.Icon = RenderIcon(text, color);
        _trayIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        old?.Dispose();
    }

    // ══════════════════════════════════════════════
    // DIALOGE
    // ══════════════════════════════════════════════

    private void ShowDetails()
    {
        if (_lastData == null)
        {
            MessageBox.Show("Noch keine Daten. Rechtsklick → Refresh Now.",
                "Claude Usage Monitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new Form
        {
            Text = "Claude.ai Usage Details",
            Size = new Size(400, _lastData.HasWeeklyLimit ? 320 : 220),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
            TopMost = true,
        };

        int y = 20;

        // Session
        AddUsageSection(dlg, ref y, "Session (5h)", _lastData.SessionPercent, _lastData.SessionResetFormatted);

        // Weekly
        if (_lastData.HasWeeklyLimit)
            AddUsageSection(dlg, ref y, "Weekly", _lastData.WeeklyPercent, _lastData.WeeklyResetFormatted);

        // Footer
        var lbl = new Label
        {
            Text = $"Updated: {_lastData.FetchedAt:HH:mm:ss} | Interval: {_settings.PollIntervalSeconds}s",
            Location = new Point(20, y), Size = new Size(350, 20),
            ForeColor = Color.FromArgb(120, 120, 130), Font = new Font("Segoe UI", 8.5f),
        };
        dlg.Controls.Add(lbl);

        dlg.ShowDialog();
    }

    private static void AddUsageSection(Form form, ref int y, string label, double pct, string resetText)
    {
        var color = pct >= 90 ? ColorCrit : pct >= 70 ? ColorWarn : ColorOk;

        form.Controls.Add(new Label
        {
            Text = $"{label}: {pct:0}%", Location = new Point(20, y), Size = new Size(350, 24),
            ForeColor = color, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
        });
        y += 28;

        var bar = new Panel
        {
            Location = new Point(20, y), Size = new Size(350, 18),
            BackColor = Color.FromArgb(45, 45, 50),
        };
        bar.Paint += (_, e) =>
        {
            var w = (int)(bar.Width * Math.Min(pct, 100) / 100);
            if (w > 0) { using var b = new SolidBrush(color); e.Graphics.FillRectangle(b, 0, 0, w, bar.Height); }
        };
        form.Controls.Add(bar);
        y += 22;

        form.Controls.Add(new Label
        {
            Text = $"Reset in: {resetText}", Location = new Point(20, y), Size = new Size(350, 20),
            ForeColor = Color.FromArgb(160, 160, 170), Font = new Font("Segoe UI", 9f),
        });
        y += 35;
    }

    private void ShowSettingsDialog()
    {
        using var dlg = new Form
        {
            Text = "Settings", Size = new Size(380, 280),
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f), TopMost = true,
        };

        int y = 20;

        dlg.Controls.Add(new Label { Text = "Polling-Intervall (Sek.):", Location = new Point(20, y), Size = new Size(200, 22) });
        var numInterval = new NumericUpDown
        {
            Location = new Point(240, y - 2), Size = new Size(100, 26),
            Minimum = 30, Maximum = 600, Value = _settings.PollIntervalSeconds,
            BackColor = Color.FromArgb(39, 39, 42), ForeColor = Color.White,
        };
        dlg.Controls.Add(numInterval);
        y += 40;

        dlg.Controls.Add(new Label { Text = "Warnung bei (%):", Location = new Point(20, y), Size = new Size(200, 22) });
        var numWarn = new NumericUpDown
        {
            Location = new Point(240, y - 2), Size = new Size(100, 26),
            Minimum = 0, Maximum = 100, Value = _settings.WarnAtPercent,
            BackColor = Color.FromArgb(39, 39, 42), ForeColor = Color.White,
        };
        dlg.Controls.Add(numWarn);
        y += 40;

        var chkAuto = new CheckBox
        {
            Text = "Beim Windows-Start starten", Location = new Point(20, y), Size = new Size(300, 24),
            Checked = _settings.AutoStart, ForeColor = Color.FromArgb(200, 200, 210),
        };
        dlg.Controls.Add(chkAuto);
        y += 45;

        var btnSave = new Button
        {
            Text = "Speichern", Location = new Point(140, y), Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(99, 102, 241),
            ForeColor = Color.White, DialogResult = DialogResult.OK,
        };
        var btnCancel = new Button
        {
            Text = "Abbrechen", Location = new Point(250, y), Size = new Size(100, 34),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 60),
            ForeColor = Color.White, DialogResult = DialogResult.Cancel,
        };
        dlg.Controls.AddRange(new Control[] { btnSave, btnCancel });
        dlg.AcceptButton = btnSave;
        dlg.CancelButton = btnCancel;

        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _settings.PollIntervalSeconds = (int)numInterval.Value;
            _settings.WarnAtPercent = (int)numWarn.Value;
            _settings.AutoStart = chkAuto.Checked;
            _settings.Save();
            _settings.ApplyAutoStart();
            _pollTimer.Interval = _settings.PollIntervalSeconds * 1000;
        }
    }

    // ══════════════════════════════════════════════
    // LIFECYCLE
    // ══════════════════════════════════════════════

    private void ExitApp()
    {
        _pollTimer.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _apiClient.Dispose();
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _pollTimer?.Dispose(); _trayIcon?.Dispose(); _contextMenu?.Dispose(); _apiClient?.Dispose(); }
        base.Dispose(disposing);
    }
}
