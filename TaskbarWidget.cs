namespace ClaudeUsageMonitor;

/// <summary>
/// A widget embedded directly in the Windows taskbar (next to the system tray).
/// Uses Win32 reparenting (WS_CHILD + SetParent) and UpdateLayeredWindow for
/// per-pixel alpha rendering. Falls back gracefully if embedding fails.
///
/// The user can drag it horizontally along the taskbar by grabbing the handle on
/// the left; the position is stored as an offset from the tray in <see cref="AppSettings"/>.
/// Rendering lives in <see cref="WidgetRenderer"/>.
/// </summary>
internal sealed class TaskbarWidget : IDisposable
{
    private static readonly WidgetLayout Layout = WidgetLayout.Taskbar;

    // ── State ────────────────────────────────────────────────────────────────
    private readonly WidgetNativeWindow _nw;
    private readonly System.Windows.Forms.Timer _timer;
    private UsageData? _data;

    public bool IsEmbedded => _nw.Embedded;

    public ContextMenuStrip? ContextMenu
    {
        get => _nw.ContextMenu;
        set => _nw.ContextMenu = value;
    }

    public Action? OnLeftClick
    {
        get => _nw.OnLeftClick;
        set => _nw.OnLeftClick = value;
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public TaskbarWidget(AppSettings settings, UsageData? initialData = null)
    {
        _nw = new WidgetNativeWindow(Layout.W, Layout.H, settings);

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => Redraw();

        if (initialData != null)
            Update(initialData);
        else
            Redraw(); // show loading state
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void Update(UsageData data)
    {
        _data = data;
        Redraw();
    }

    /// <summary>
    /// Called when the TaskbarCreated message is received (Explorer restarted).
    /// Recreates the window and re-embeds it in the new taskbar.
    /// </summary>
    public void Reattach()
    {
        _nw.Reattach();
        if (_nw.Embedded)
            Redraw();
    }

    /// <summary>
    /// Called on resume from standby. Re-runs position math so the widget
    /// doesn't drift over the "show hidden icons" arrow after the taskbar relays out.
    /// </summary>
    public void Reposition()
    {
        if (_nw.RepositionInTaskbar())
            Redraw();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _nw.Dispose();
    }

    // ── Position helpers (pure, testable) ─────────────────────────────────────

    /// <summary>
    /// Clamps the widget's x (in taskbar-client coords) so it stays between the
    /// left edge (0) and flush against the tray (<paramref name="maxX"/>).
    /// </summary>
    internal static int ClampWidgetX(int desiredX, int maxX)
        => Math.Clamp(desiredX, 0, Math.Max(0, maxX));

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (!_nw.Embedded) return;
        _nw.Paint(Win32Interop.IsLightMode(), _data);
        ScheduleNextRedraw();
    }

    private void ScheduleNextRedraw()
    {
        _timer.Stop();
        if (_data == null) return;

        var delay = WidgetRenderer.NextDisplayChange(_data);
        _timer.Interval = Math.Clamp((int)delay.TotalMilliseconds, 1_000, 60_000);
        _timer.Start();
    }

    // ════════════════════════════════════════════════════════════════════════
    // WidgetNativeWindow — low-level Win32 window that embeds in the taskbar
    // ════════════════════════════════════════════════════════════════════════

    private sealed class WidgetNativeWindow : NativeWindow, IDisposable
    {
        private const int WM_MOUSEMOVE      = 0x0200;
        private const int WM_LBUTTONDOWN    = 0x0201;
        private const int WM_LBUTTONUP      = 0x0202;
        private const int WM_RBUTTONUP      = 0x0205;
        private const int WM_CAPTURECHANGED = 0x0215;
        private const int DragThreshold  = 3;

        private readonly int _w, _h;
        private readonly AppSettings _settings;

        public bool Embedded { get; private set; }
        public ContextMenuStrip? ContextMenu { get; set; }
        public Action? OnLeftClick { get; set; }

        // Live geometry, kept in sync whenever we MoveWindow.
        private int _currentX;
        private int _y;

        // Drag state
        private bool  _dragging;   // press started on the grip
        private bool  _moved;
        private Point _dragStartCursor;
        private int   _dragStartX;
        private int   _dragMaxX;

        public WidgetNativeWindow(int w, int h, AppSettings settings)
        {
            _w = w; _h = h; _settings = settings;

            var cp = MakeCreateParams(w, h);
            try
            {
                CreateHandle(cp);
                Embedded = TryEmbedInTaskbar();
            }
            catch
            {
                Embedded = false;
            }
        }

        private static CreateParams MakeCreateParams(int w, int h) => new()
        {
            // Start as POPUP (top-level); changed to CHILD after creation
            Style   = unchecked((int)(Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE)),
            ExStyle = unchecked((int)(Win32Interop.WS_EX_TOOLWINDOW
                                    | Win32Interop.WS_EX_LAYERED
                                    | Win32Interop.WS_EX_NOACTIVATE)),
            Width   = w,
            Height  = h,
            X       = -2000, // off-screen until embedded
            Y       = -2000,
            Caption = "",
        };

        /// <summary>
        /// Reads the current tray geometry. <paramref name="maxX"/> is the widget's
        /// x when flush against the tray (also the anchor the saved offset counts back from).
        /// </summary>
        private static bool TryGetGeometry(int w, int h, out int maxX, out int y)
        {
            maxX = 0; y = 0;

            var taskbar = Win32Interop.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            var trayNotify = Win32Interop.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify == IntPtr.Zero) return false;

            if (!Win32Interop.GetWindowRect(trayNotify, out var trayRect) ||
                !Win32Interop.GetWindowRect(taskbar,    out var taskbarRect))
                return false;

            maxX = trayRect.Left - taskbarRect.Left - w;
            y    = (taskbarRect.Height - h) / 2;
            return true;
        }

        private bool TryEmbedInTaskbar()
        {
            if (Handle == IntPtr.Zero) return false;

            var taskbar = Win32Interop.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            // Rewrite window style: POPUP → CHILD | CLIPSIBLINGS
            var style = Win32Interop.GetWindowLong(Handle, Win32Interop.GWL_STYLE);
            style = (style & ~unchecked((int)Win32Interop.WS_POPUP))
                  | unchecked((int)(Win32Interop.WS_CHILD | Win32Interop.WS_CLIPSIBLINGS));
            Win32Interop.SetWindowLong(Handle, Win32Interop.GWL_STYLE, style);

            if (Win32Interop.SetParent(Handle, taskbar) == IntPtr.Zero) return false;

            ApplyPosition();
            Win32Interop.ShowWindow(Handle, Win32Interop.SW_SHOWNOACTIVATE);
            return true;
        }

        /// <summary>
        /// Positions the widget at (tray anchor − saved offset), clamped to the taskbar.
        /// </summary>
        private bool ApplyPosition()
        {
            if (!TryGetGeometry(_w, _h, out int maxX, out int y)) return false;

            int x = ClampWidgetX(maxX - _settings.WidgetOffsetX, maxX);
            _currentX = x;
            _y = y;
            Win32Interop.MoveWindow(Handle, x, y, _w, _h, true);
            return true;
        }

        public void Paint(bool lightMode, UsageData? data)
            => LayeredWindow.Paint(Handle, _w, _h,
                   g => WidgetRenderer.Render(g, WidgetLayout.Taskbar, lightMode, data,
                                              AccentPalette.Ok(_settings.Accent)));

        /// <summary>Re-applies the position after taskbar relayout (resume from standby).</summary>
        public bool RepositionInTaskbar()
        {
            if (Handle == IntPtr.Zero || !Embedded) return false;
            return ApplyPosition();
        }

        /// <summary>
        /// Re-embeds the widget after Explorer restarts (TaskbarCreated message).
        /// The old child window was destroyed with Shell_TrayWnd, so we release the
        /// stale handle reference and create a fresh window before re-embedding.
        /// </summary>
        public void Reattach()
        {
            Embedded = false;
            if (Handle != IntPtr.Zero)
                ReleaseHandle(); // don't call DestroyWindow on an already-destroyed handle

            try
            {
                CreateHandle(MakeCreateParams(_w, _h));
                Embedded = TryEmbedInTaskbar();
            }
            catch
            {
                Embedded = false;
            }
        }

        // ── Input: grip drag + click + context menu ──────────────────────────

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    OnLButtonDown(unchecked((int)m.LParam.ToInt64()));
                    return;

                case WM_MOUSEMOVE:
                    OnMouseMove();
                    return;

                case WM_LBUTTONUP:
                    OnLButtonUp();
                    return;

                case WM_RBUTTONUP:
                    ShowContextMenu(unchecked((int)m.LParam.ToInt64()));
                    return;

                case WM_CAPTURECHANGED:
                    OnCaptureChanged();
                    break; // fall through to base
            }
            base.WndProc(ref m);
        }

        private void OnLButtonDown(int lparam)
        {
            int clientX = (short)(lparam & 0xFFFF);
            Win32Interop.SetCapture(Handle);

            // Only the grip strip starts a drag; a press elsewhere is a click (popup).
            if (clientX < Layout.PadL + Layout.GripW && TryGetGeometry(_w, _h, out int maxX, out int y))
            {
                _dragging = true;
                _moved    = false;
                _dragMaxX = maxX;
                _y        = y;
                _dragStartX = _currentX;
                Win32Interop.GetCursorPos(out var c);
                _dragStartCursor = new Point(c.X, c.Y);
            }
        }

        private void OnMouseMove()
        {
            if (!_dragging) return;

            Win32Interop.GetCursorPos(out var c);
            int dx = c.X - _dragStartCursor.X;
            if (Math.Abs(dx) > DragThreshold) _moved = true;

            int x = ClampWidgetX(_dragStartX + dx, _dragMaxX);
            _currentX = x;
            Win32Interop.MoveWindow(Handle, x, _y, _w, _h, false);
        }

        private void OnLButtonUp()
        {
            bool wasDragging = _dragging;
            _dragging = false;
            Win32Interop.ReleaseCapture();

            if (wasDragging && _moved)
            {
                // Store how far left of the tray anchor the widget now sits.
                _settings.WidgetOffsetX = _dragMaxX - _currentX;
                _settings.Save();
            }
            else
            {
                OnLeftClick?.Invoke();
            }
        }

        /// <summary>
        /// Capture was taken away mid-drag (Alt-Tab, UAC prompt, another SetCapture).
        /// Without this, _dragging stays true and a later hover moves the widget
        /// with no button pressed. Commit the position like a normal drag end,
        /// but never treat it as a click.
        /// </summary>
        private void OnCaptureChanged()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_moved)
            {
                _settings.WidgetOffsetX = _dragMaxX - _currentX;
                _settings.Save();
            }
        }

        private void ShowContextMenu(int lparam)
        {
            if (ContextMenu == null) return;
            var pt = new Win32Interop.POINT
            {
                X = (short)(lparam & 0xFFFF),
                Y = (short)((lparam >> 16) & 0xFFFF),
            };
            Win32Interop.ClientToScreen(Handle, ref pt);
            ContextMenu.Show(pt.X, pt.Y);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }
}
