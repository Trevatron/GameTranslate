using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GameTranslate.Models;

namespace GameTranslate.Services;

public static class ImagePreprocessor
{
    private const int PaddingPx = 22;
    private const int MaxOcrDimension = 3200;

    /// <summary>
    /// Prepares a captured subtitle region for Windows OCR:
    /// - Upscales small subtitle strips for crisp glyph recognition
    /// - Automatically detects dark-background games with glowing white/yellow text (e.g. pixel-art / retro / cinema subtitles)
    ///   and strips the glow/bloom halo using smooth anti-aliased inversion (black text on pure white)
    /// - Adds clean border padding so edge letters are never clipped.
    /// </summary>
    public static Bitmap PrepareForOcr(Bitmap source, OcrFilterMode mode, int whiteThreshold = 175)
    {
        double scale = 1.0;
        if (source.Height < 170)
            scale = 2.0;
        else if (source.Height < 260)
            scale = 1.5;

        int scaledW = (int)Math.Round(source.Width * scale);
        int scaledH = (int)Math.Round(source.Height * scale);

        if (scaledW + PaddingPx * 2 > MaxOcrDimension || scaledH + PaddingPx * 2 > MaxOcrDimension)
        {
            double clamp = Math.Min(
                (double)(MaxOcrDimension - PaddingPx * 2) / source.Width,
                (double)(MaxOcrDimension - PaddingPx * 2) / source.Height);
            scaledW = Math.Max(1, (int)(source.Width * clamp));
            scaledH = Math.Max(1, (int)(source.Height * clamp));
        }

        Bitmap working;
        bool needScale = Math.Abs(scale - 1.0) > 0.01 || scaledW != source.Width || scaledH != source.Height;
        if (needScale)
        {
            working = new Bitmap(scaledW, scaledH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(working);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.DrawImage(source, new Rectangle(0, 0, scaledW, scaledH));
        }
        else
        {
            // Fast direct clone without expensive Bicubic resampling when already full resolution
            working = source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
        }

        Color padColor = Color.Black;
        switch (mode)
        {
            case OcrFilterMode.WhiteYellowText:
                ApplySoftBloomStripWhiteYellowFilter(working, Math.Clamp(whiteThreshold, 100, 240));
                padColor = Color.White;
                break;

            case OcrFilterMode.HighContrast:
                ApplyAdaptiveContrastBinarization(working);
                padColor = Color.White;
                break;

            case OcrFilterMode.Auto:
                // If the captured frame is a dark game background with bright white/yellow text
                // (common in retro/pixel-art games, RPGs, cutscenes, and dark subtitle bars),
                // smooth anti-aliased bloom-strip inversion dramatically improves Windows OCR accuracy.
                if (LooksLikeDarkBackgroundBrightText(working))
                {
                    ApplySoftBloomStripWhiteYellowFilter(working, 168);
                    padColor = Color.White;
                }
                else
                {
                    ApplyMildContrastBoost(working);
                    padColor = SampleEdgeColor(working);
                }
                break;

            case OcrFilterMode.Original:
            default:
                padColor = SampleEdgeColor(working);
                break;
        }

        var padded = new Bitmap(scaledW + PaddingPx * 2, scaledH + PaddingPx * 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(padded))
        {
            g.Clear(padColor);
            g.DrawImageUnscaled(working, PaddingPx, PaddingPx);
        }

        working.Dispose();
        return padded;
    }

    /// <summary>
    /// Samples the image on a fast grid to check whether it's predominantly dark (&gt;= 62% luma &lt; 95)
    /// with crisp bright white/yellow foreground pixels (&gt;= 0.6% luma &gt;= 190).
    /// </summary>
    private static unsafe bool LooksLikeDarkBackgroundBrightText(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int stepX = Math.Max(1, bmp.Width / 80);
            int stepY = Math.Max(1, bmp.Height / 60);

            int total = 0;
            int dark = 0;
            int brightText = 0;

            for (int y = 0; y < bmp.Height; y += stepY)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < bmp.Width; x += stepX)
                {
                    byte* px = row + (x * 4);
                    int b = px[0], g = px[1], r = px[2];
                    int luma = (r * 77 + g * 150 + b * 29) >> 8;
                    total++;

                    if (luma < 95)
                    {
                        dark++;
                    }
                    else if (luma >= 190)
                    {
                        int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                        bool isWhite = chroma <= 70;
                        bool isYellow = r >= 185 && g >= 160 && (r - b) >= 50;
                        if (isWhite || isYellow)
                            brightText++;
                    }
                }
            }

            if (total == 0) return false;
            double darkRatio = (double)dark / total;
            double brightRatio = (double)brightText / total;
            return darkRatio >= 0.60 && brightRatio >= 0.005 && brightRatio <= 0.35;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Strips dark backgrounds and glowing CRT/bloom halos around bright white or yellow game text
    /// while preserving smooth anti-aliased glyph edges (black text on pure white background).
    /// </summary>
    private static unsafe void ApplySoftBloomStripWhiteYellowFilter(Bitmap bmp, int lowCut)
    {
        int highCut = Math.Min(245, lowCut + 58);
        double span = Math.Max(1.0, highCut - lowCut);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int w = bmp.Width;
            int h = bmp.Height;

            for (int y = 0; y < h; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + (x * 4);
                    int b = px[0];
                    int g = px[1];
                    int r = px[2];

                    int luma = (r * 77 + g * 150 + b * 29) >> 8;
                    int maxC = Math.Max(r, Math.Max(g, b));
                    int minC = Math.Min(r, Math.Min(g, b));
                    int chroma = maxC - minC;

                    // Keep neutral white/gray text OR bright yellow subtitle/menu text;
                    // suppress red/blue/purple game sprites and backgrounds cleanly
                    bool isValidTextHue = chroma <= 72 || (r >= lowCut && g >= (lowCut - 20) && (r - b) >= 50);

                    byte val = 255;
                    if (isValidTextHue && luma > lowCut)
                    {
                        double t = (luma - lowCut) / span;
                        if (t > 1.0) t = 1.0;
                        val = (byte)Math.Round((1.0 - t) * 255.0);
                    }

                    px[0] = val;
                    px[1] = val;
                    px[2] = val;
                    px[3] = 255;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static unsafe void ApplyAdaptiveContrastBinarization(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int w = bmp.Width;
            int h = bmp.Height;
            int total = w * h;

            var hist = new int[256];
            for (int y = 0; y < h; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + (x * 4);
                    int luma = (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
                    hist[luma]++;
                }
            }

            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * hist[i];

            double sumB = 0;
            int wB = 0;
            double maxVar = 0;
            int threshold = 128;

            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                int wF = total - wB;
                if (wF == 0) break;

                sumB += t * hist[t];
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > maxVar)
                {
                    maxVar = between;
                    threshold = t;
                }
            }

            int brightCount = 0;
            for (int i = threshold; i < 256; i++) brightCount += hist[i];
            bool lightTextOnDark = brightCount < total / 2;

            for (int y = 0; y < h; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + (x * 4);
                    int luma = (px[2] * 77 + px[1] * 150 + px[0] * 29) >> 8;
                    bool isForeground = lightTextOnDark ? (luma >= threshold) : (luma < threshold);
                    byte val = isForeground ? (byte)0 : (byte)255;
                    px[0] = val;
                    px[1] = val;
                    px[2] = val;
                    px[3] = 255;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static unsafe void ApplyMildContrastBoost(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stride = data.Stride;
            int w = bmp.Width;
            int h = bmp.Height;

            var lut = new byte[256];
            const double contrast = 1.28;
            for (int i = 0; i < 256; i++)
            {
                int v = (int)Math.Round(((i - 128.0) * contrast) + 128.0);
                lut[i] = (byte)Math.Clamp(v, 0, 255);
            }

            for (int y = 0; y < h; y++)
            {
                byte* row = scan0 + (y * stride);
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + (x * 4);
                    px[0] = lut[px[0]];
                    px[1] = lut[px[1]];
                    px[2] = lut[px[2]];
                    px[3] = 255;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static Color SampleEdgeColor(Bitmap bmp)
    {
        if (bmp.Width < 2 || bmp.Height < 2)
            return Color.Black;

        Color c1 = bmp.GetPixel(0, 0);
        Color c2 = bmp.GetPixel(bmp.Width - 1, 0);
        Color c3 = bmp.GetPixel(0, bmp.Height - 1);
        Color c4 = bmp.GetPixel(bmp.Width - 1, bmp.Height - 1);

        int r = (c1.R + c2.R + c3.R + c4.R) / 4;
        int g = (c1.G + c2.G + c3.G + c4.G) / 4;
        int b = (c1.B + c2.B + c3.B + c4.B) / 4;
        return Color.FromArgb(255, r, g, b);
    }
}
