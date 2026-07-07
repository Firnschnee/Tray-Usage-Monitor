using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClaudeUsageMonitor;

/// <summary>
/// Shared per-pixel-alpha painter for layered windows.
/// Creates a 32-bit top-down DIB section, lets the caller draw into it with GDI+,
/// then hands the result to the window manager via UpdateLayeredWindow.
/// Used by both the taskbar widget and the floating overlay.
/// </summary>
internal static class LayeredWindow
{
    public static void Paint(IntPtr handle, int w, int h, Action<Graphics> draw)
    {
        if (handle == IntPtr.Zero) return;

        var bmiHeader = new Win32Interop.BITMAPINFOHEADER
        {
            biSize        = Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
            biWidth       = w,
            biHeight      = -h,   // negative = top-down scan order
            biPlanes      = 1,
            biBitCount    = 32,
            biCompression = 0,    // BI_RGB
        };

        var hdcScreen = Win32Interop.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero) return;

        var hdcMem  = Win32Interop.CreateCompatibleDC(hdcScreen);
        var hBitmap = Win32Interop.CreateDIBSection(hdcScreen, ref bmiHeader, 0,
                                                    out var pBits, IntPtr.Zero, 0);

        if (hdcMem == IntPtr.Zero || hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
        {
            if (hdcMem  != IntPtr.Zero) Win32Interop.DeleteDC(hdcMem);
            if (hBitmap != IntPtr.Zero) Win32Interop.DeleteObject(hBitmap);
            Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
            return;
        }

        var hOld = Win32Interop.SelectObject(hdcMem, hBitmap);

        try
        {
            // GDI+ draws directly into the DIB section memory (stride = w * 4, top-down).
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppArgb, pBits))
            using (var gfx = Graphics.FromImage(bmp))
            {
                draw(gfx);
            }

            // GDI+ objects disposed above — safe to hand the DIB to UpdateLayeredWindow now.
            var blend = new Win32Interop.BLENDFUNCTION
            {
                BlendOp             = Win32Interop.AC_SRC_OVER,
                BlendFlags          = 0,
                SourceConstantAlpha = 255,
                AlphaFormat         = Win32Interop.AC_SRC_ALPHA,
            };
            var ptSrc = new Win32Interop.POINT { X = 0, Y = 0 };
            var sz    = new Win32Interop.SIZE   { cx = w, cy = h };

            // IntPtr.Zero for pptDst: keep the position managed by the caller (MoveWindow).
            Win32Interop.UpdateLayeredWindow(
                handle, hdcScreen, IntPtr.Zero, ref sz,
                hdcMem, ref ptSrc, 0, ref blend, Win32Interop.ULW_ALPHA);
        }
        finally
        {
            Win32Interop.SelectObject(hdcMem, hOld);
            Win32Interop.DeleteObject(hBitmap);
            Win32Interop.DeleteDC(hdcMem);
            Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }
}
