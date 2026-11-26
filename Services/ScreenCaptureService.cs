using System.Drawing;
using System.Drawing.Imaging;
using GameTranslate.Models;
using GameTranslate.Native;

namespace GameTranslate.Services;

public sealed class ScreenCaptureService
{
    /// <summary>
    /// Captures a physical pixel rectangle from the virtual desktop using fast Win32 BitBlt.
    /// Any window marked with WDA_EXCLUDEFROMCAPTURE is automatically omitted by Windows DWM.
    /// </summary>
    public Bitmap CaptureRegion(ScreenRect region)
    {
        if (!region.IsValid)
            throw new ArgumentException("Região de captura inválida.", nameof(region));

        IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            throw new InvalidOperationException("Não foi possível obter o DC da tela.");

        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcMem = Win32.CreateCompatibleDC(hdcScreen);
            hBitmap = Win32.CreateCompatibleBitmap(hdcScreen, region.Width, region.Height);
            hOld = Win32.SelectObject(hdcMem, hBitmap);

            Win32.BitBlt(
                hdcMem, 0, 0, region.Width, region.Height,
                hdcScreen, region.X, region.Y,
                Win32.SRCCOPY);

            using var raw = Image.FromHbitmap(hBitmap);
            var result = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(result);
            g.DrawImageUnscaled(raw, 0, 0);
            return result;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
                Win32.SelectObject(hdcMem, hOld);
            if (hBitmap != IntPtr.Zero)
                Win32.DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero)
                Win32.DeleteDC(hdcMem);
            Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    /// <summary>
    /// Computes an ultra-fast 64x16 fingerprint for frame-diff checks.
    /// On predominantly dark game screens (e.g. animated dark waves/particles behind white/yellow text),
    /// dark background pixels (&lt; 95 luma) are clamped to 0 so moving dark backgrounds NEVER trigger
    /// wasted OCR cycles or delay spikes when the subtitle text itself hasn't changed!
    /// </summary>
    public static unsafe byte[] ComputeFingerprint(Bitmap bitmap)
    {
        const int gridW = 64;
        const int gridH = 16;
        var fp = new byte[gridW * gridH];

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int w = bitmap.Width;
            int h = bitmap.Height;
            int darkCount = 0;

            for (int gy = 0; gy < gridH; gy++)
            {
                int sy = Math.Min(h - 1, (gy * h) / gridH);
                byte* row = scan0 + (sy * stride);
                int dstRow = gy * gridW;

                for (int gx = 0; gx < gridW; gx++)
                {
                    int sx = Math.Min(w - 1, (gx * w) / gridW);
                    byte* px = row + (sx * 4);
                    int luma = (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
                    if (luma < 95) darkCount++;
                    fp[dstRow + gx] = (byte)luma;
                }
            }

            // If >= 60% of sampled pixels are dark, clamp dark background noise to 0
            // so animated dark backgrounds never cause false frame-change triggers.
            if (darkCount >= (gridW * gridH * 60) / 100)
            {
                for (int i = 0; i < fp.Length; i++)
                {
                    if (fp[i] < 105)
                        fp[i] = 0;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return fp;
    }

    public static double ComputeMeanAbsDiff(byte[]? previous, byte[] current)
    {
        if (previous == null || previous.Length != current.Length)
            return 255.0;

        long sum = 0;
        for (int i = 0; i < current.Length; i++)
        {
            int d = previous[i] - current[i];
            sum += d < 0 ? -d : d;
        }

        return (double)sum / current.Length;
    }
}
