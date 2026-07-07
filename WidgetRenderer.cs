using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>
/// Geometry + font sizes for the taskbar widget. Grouped here so the renderer
/// and the window keep a single source of truth for sizing.
/// </summary>
internal readonly struct WidgetLayout
{
    public int W { get; init; }
    public int H { get; init; }
    public int GripW { get; init; }       // left grab-handle strip width
    public int PadL { get; init; }
    public int LabelW { get; init; }
    public int LabelBarGap { get; init; }
    public int BarW { get; init; }
    public int BarH { get; init; }
    public int BarTextGap { get; init; }
    public int PadR { get; init; }
    public int Row1Y { get; init; }
    public int Row2Y { get; init; }
    public int BarRadius { get; init; }
    public float LabelFontSize { get; init; }
    public float TextFontSize { get; init; }

    /// <summary>X where the content (labels/bars) starts, i.e. right of the grip.</summary>
    public int ContentX => PadL + GripW;
    public int TextW => W - ContentX - LabelW - LabelBarGap - BarW - BarTextGap - PadR;

    public static readonly WidgetLayout Taskbar = new()
    {
        W = 300, H = 46, GripW = 12,
        PadL = 4, LabelW = 24, LabelBarGap = 5,
        BarW = 168, BarH = 15, BarTextGap = 6, PadR = 6,
        Row1Y = 6, Row2Y = 25, BarRadius = 2,
        LabelFontSize = 9.5f, TextFontSize = 9.5f,
    };
}

/// <summary>
/// Draws the two-row usage widget (session + weekly) plus a left grab handle
/// into a <see cref="Graphics"/>. Pure rendering: no window, no Win32.
/// </summary>
internal static class WidgetRenderer
{
    private static readonly Color FillWarn = Color.FromArgb(251, 191,  36); // yellow
    private static readonly Color FillCrit = Color.FromArgb(239,  68,  68); // red

    private static Color BgColor(bool light)    => light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x1C, 0x1C, 0x1C);
    private static Color TrackColor(bool light) => light ? Color.FromArgb(0xAA, 0xAA, 0xAA) : Color.FromArgb(0x44, 0x44, 0x44);
    private static Color TextColor(bool light)  => light ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0x88, 0x88, 0x88);
    private static Color FillColor(double pct, Color ok) => pct >= 90 ? FillCrit : pct >= 75 ? FillWarn : ok;

    internal static void Render(Graphics g, WidgetLayout L, bool light, UsageData? data, Color okColor)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        // Background: alpha=1 (near-invisible, but hit-testable for clicks/drag/right-click)
        using (var bgBrush = new SolidBrush(Color.FromArgb(1, BgColor(light))))
            g.FillRectangle(bgBrush, 0, 0, L.W, L.H);

        var textClr  = TextColor(light);
        var trackClr = TrackColor(light);

        DrawGrip(g, L, textClr);

        if (data == null)
        {
            using var fb = new SolidBrush(textClr);
            using var ff = new Font("Segoe UI", L.TextFontSize);
            g.DrawString("...", ff, fb, new RectangleF(L.ContentX, L.H / 2f - 8, L.W, 16));
            return;
        }

        DrawRow(g, L, L.Row1Y, "5h", data.SessionPercent, data.SessionResetIn, textClr, trackClr, okColor);

        if (data.HasWeekly)
            DrawRow(g, L, L.Row2Y, "7d", data.WeeklyPercent, data.WeeklyResetIn, textClr, trackClr, okColor);
        else
            DrawRow(g, L, L.Row2Y, "7d", -1, TimeSpan.Zero, textClr, trackClr, okColor);
    }

    /// <summary>Two columns of dots on the far left — the drag handle.</summary>
    private static void DrawGrip(Graphics g, WidgetLayout L, Color textClr)
    {
        using var brush = new SolidBrush(Color.FromArgb(110, textClr));
        float r  = 1.4f;
        float cx1 = L.PadL + 3f;
        float cx2 = L.PadL + 7f;
        int   midY = L.H / 2;
        foreach (var dy in new[] { -12, -4, 4, 12 })
        {
            float cy = midY + dy;
            g.FillEllipse(brush, cx1 - r, cy - r, r * 2, r * 2);
            g.FillEllipse(brush, cx2 - r, cy - r, r * 2, r * 2);
        }
    }

    private static void DrawRow(Graphics g, WidgetLayout L, int rowY, string label,
                                double pct, TimeSpan resetIn,
                                Color textClr, Color trackClr, Color okColor)
    {
        int contentX = L.ContentX;

        // Label ("5h" / "7d"). Text rect is taller than the bar (same center) to give
        // the larger fonts vertical slack without clipping descenders.
        using var labelFont  = new Font("Segoe UI", L.LabelFontSize, FontStyle.Bold);
        using var labelBrush = new SolidBrush(pct >= 0 ? textClr : Color.FromArgb(60, textClr));
        using var centerFmt  = new StringFormat
        {
            Alignment     = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(label, labelFont, labelBrush,
                     new RectangleF(contentX, rowY - 3, L.LabelW, L.BarH + 6), centerFmt);

        int barX  = contentX + L.LabelW + L.LabelBarGap;
        int textX = barX + L.BarW + L.BarTextGap;

        if (pct >= 0)
        {
            DrawSolidBar(g, L, barX, rowY, pct, trackClr, okColor);

            string txt = $"{pct:0}% {FormatSpanShort(resetIn)}";
            using var textFont  = new Font("Segoe UI", L.TextFontSize);
            using var textBrush = new SolidBrush(textClr);
            g.DrawString(txt, textFont, textBrush,
                         new RectangleF(textX, rowY - 3, L.TextW, L.BarH + 6), centerFmt);
        }
        else
        {
            DrawSolidBar(g, L, barX, rowY, 0, trackClr, okColor);
            using var textFont  = new Font("Segoe UI", L.TextFontSize);
            using var textBrush = new SolidBrush(Color.FromArgb(60, textClr));
            g.DrawString("--", textFont, textBrush,
                         new RectangleF(textX, rowY - 3, L.TextW, L.BarH + 6), centerFmt);
        }
    }

    private static void DrawSolidBar(Graphics g, WidgetLayout L, int x, int y, double pct, Color trackClr, Color okColor)
    {
        var barRect = new RectangleF(x, y, L.BarW, L.BarH);

        using var path = RoundedRect(barRect, L.BarRadius);
        using (var trackBrush = new SolidBrush(trackClr))
            g.FillPath(trackBrush, path);

        float fillW = L.BarW * (float)Math.Clamp(pct, 0, 100) / 100f;
        if (fillW > 0)
        {
            var state = g.Save();
            g.SetClip(new RectangleF(x, y, fillW, L.BarH), CombineMode.Intersect);
            using (var fillBrush = new SolidBrush(FillColor(pct, okColor)))
                g.FillPath(fillBrush, path);
            g.Restore(state);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, int radius)
    {
        float d    = radius * 2;
        var   path = new GraphicsPath();
        path.AddArc(r.Left,      r.Top,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d,   d, d,   0, 90);
        path.AddArc(r.Left,      r.Bottom - d,   d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    private static string FormatSpanShort(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero)      return "--";
        if (ts.TotalDays  >= 1)       return $"{(int)ts.TotalDays}d";
        if (ts.TotalHours >= 1)       return $"{(int)ts.TotalHours}h";
        return $"{ts.Minutes}m";
    }

    // ── Smart redraw scheduling ──────────────────────────────────────────────
    // Calculates how long until the displayed countdown text next changes,
    // so consumers only redraw when the display actually changes.

    internal static TimeSpan NextDisplayChange(UsageData data)
    {
        var candidates = new List<TimeSpan>();
        if (data.SessionResetsAt.HasValue)
            candidates.Add(data.SessionResetIn);
        if (data.HasWeekly && data.WeeklyResetsAt.HasValue)
            candidates.Add(data.WeeklyResetIn);

        if (candidates.Count == 0) return TimeSpan.FromMinutes(1);

        var minNext = TimeSpan.MaxValue;
        foreach (var span in candidates)
        {
            var next = NextChangeForSpan(span);
            if (next < minNext) minNext = next;
        }
        return minNext;
    }

    private static TimeSpan NextChangeForSpan(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return TimeSpan.FromMinutes(1);

        if (remaining.TotalDays >= 1)
        {
            var fracDay = remaining - TimeSpan.FromDays((int)remaining.TotalDays);
            return fracDay > TimeSpan.Zero ? fracDay : TimeSpan.FromDays(1);
        }
        if (remaining.TotalHours >= 1)
        {
            var fracHour = remaining - TimeSpan.FromHours((int)remaining.TotalHours);
            return fracHour > TimeSpan.Zero ? fracHour : TimeSpan.FromHours(1);
        }
        var fracMin = remaining - TimeSpan.FromMinutes((int)remaining.TotalMinutes);
        return fracMin > TimeSpan.FromSeconds(1) ? fracMin : TimeSpan.FromMinutes(1);
    }
}
