using System.Drawing;
using System.Drawing.Imaging;
using GameTranslate.Models;
using GameTranslate.Services;
using Xunit;

namespace GameTranslate.Tests;

public sealed class ImagePreprocessorAndCaptureTests
{
    [Fact]
    public void Fingerprint_IdenticalFrames_HaveZeroDiff()
    {
        using var bmp1 = CreateSubtitleBitmap(640, 140, Color.FromArgb(20, 22, 35), "Hello world!");
        using var bmp2 = CreateSubtitleBitmap(640, 140, Color.FromArgb(20, 22, 35), "Hello world!");

        byte[] fp1 = ScreenCaptureService.ComputeFingerprint(bmp1);
        byte[] fp2 = ScreenCaptureService.ComputeFingerprint(bmp2);

        Assert.Equal(0.0, ScreenCaptureService.ComputeMeanAbsDiff(fp1, fp2));
    }

    [Fact]
    public void Fingerprint_AnimatedDarkBackgroundWaves_DoNotTriggerFalseDiff()
    {
        // Two frames with identical white subtitle text, but shifting dark purple waves (luma ~20 vs ~65)
        using var frameA = CreateSubtitleBitmap(640, 200, Color.FromArgb(22, 16, 40), "Please give it a gift.");
        using var frameB = CreateSubtitleBitmap(640, 200, Color.FromArgb(62, 48, 86), "Please give it a gift.");

        byte[] fpA = ScreenCaptureService.ComputeFingerprint(frameA);
        byte[] fpB = ScreenCaptureService.ComputeFingerprint(frameB);
        double diff = ScreenCaptureService.ComputeMeanAbsDiff(fpA, fpB);

        Assert.True(diff < 1.0, $"Expected dark animated waves to be ignored (< 1.0), got {diff:F2}");
    }

    [Fact]
    public void Fingerprint_ChangedSubtitleText_TriggersSignificantDiff()
    {
        using var frameA = CreateSubtitleBitmap(640, 140, Color.FromArgb(15, 15, 25), "First dialogue line.");
        using var frameB = CreateSubtitleBitmap(640, 140, Color.FromArgb(15, 15, 25), "A COMPLETELY DIFFERENT SENTENCE HERE!");

        byte[] fpA = ScreenCaptureService.ComputeFingerprint(frameA);
        byte[] fpB = ScreenCaptureService.ComputeFingerprint(frameB);
        double diff = ScreenCaptureService.ComputeMeanAbsDiff(fpA, fpB);

        Assert.True(diff > 2.8, $"Expected changed text to exceed threshold 2.8, got {diff:F2}");
    }

    [Fact]
    public void PrepareForOcr_SmallSubtitleBar_Upscales2xAndAddsPadding()
    {
        using var small = CreateSubtitleBitmap(400, 80, Color.Black, "Small subtitle");
        using var processed = ImagePreprocessor.PrepareForOcr(small, OcrFilterMode.Original);

        // 400*2 + 22*2 = 844, 80*2 + 22*2 = 204
        Assert.Equal(844, processed.Width);
        Assert.Equal(204, processed.Height);
    }

    [Theory]
    [InlineData(OcrFilterMode.Auto)]
    [InlineData(OcrFilterMode.WhiteYellowText)]
    [InlineData(OcrFilterMode.HighContrast)]
    [InlineData(OcrFilterMode.Original)]
    public void PrepareForOcr_AllModes_ProduceValidNonEmptyBitmaps(OcrFilterMode mode)
    {
        using var src = CreateSubtitleBitmap(600, 120, Color.FromArgb(25, 20, 40), "Testing all OCR filter modes");
        using var result = ImagePreprocessor.PrepareForOcr(src, mode, 175);

        Assert.NotNull(result);
        Assert.True(result.Width > src.Width);
        Assert.True(result.Height > src.Height);
        Assert.Equal(PixelFormat.Format32bppArgb, result.PixelFormat);
    }

    [Fact]
    public void PrepareForOcr_AutoModeOnDarkGameScreen_StripsDarkBackgroundAndSuppressesRedSprite()
    {
        using var src = new Bitmap(400, 280, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(25, 18, 42)); // Dark purple game background
            // Bright red heart sprite (should be suppressed to white background)
            using var redBrush = new SolidBrush(Color.FromArgb(220, 25, 30));
            g.FillRectangle(redBrush, 30, 100, 40, 40);
            // Bright white text block (should invert to dark/black text)
            g.FillRectangle(Brushes.White, 120, 100, 180, 40);
        }

        using var processed = ImagePreprocessor.PrepareForOcr(src, OcrFilterMode.Auto);

        // Padding is 22px, scale = 1.0 (height >= 260)
        Color bgPixel = processed.GetPixel(22 + 5, 22 + 5);
        Color redSpritePixel = processed.GetPixel(22 + 45, 22 + 120);
        Color whiteTextPixel = processed.GetPixel(22 + 180, 22 + 120);

        Assert.Equal(255, bgPixel.R);         // Dark background became clean white
        Assert.Equal(255, redSpritePixel.R);  // Red sprite was suppressed
        Assert.True(whiteTextPixel.R < 30);   // White text became crisp black
    }

    [Fact]
    public void PrepareForOcr_HugeImage_ClampsWithinMaxOcrDimension()
    {
        using var huge = new Bitmap(3840, 120, PixelFormat.Format32bppArgb);
        using var processed = ImagePreprocessor.PrepareForOcr(huge, OcrFilterMode.Original);

        Assert.True(processed.Width <= 3200);
        Assert.True(processed.Height <= 3200);
    }

    private static Bitmap CreateSubtitleBitmap(int w, int h, Color bg, string text)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(bg);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using var font = new Font("Arial", 24, FontStyle.Bold);
        g.DrawString(text, font, Brushes.White, 24, (h - 40) / 2f);
        return bmp;
    }
}
