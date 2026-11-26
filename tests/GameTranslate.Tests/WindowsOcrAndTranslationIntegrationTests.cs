using System.Drawing;
using System.Drawing.Imaging;
using GameTranslate.Models;
using GameTranslate.Services;
using Xunit;

namespace GameTranslate.Tests;

[Trait("Category", "Integration")]
public sealed class WindowsOcrAndTranslationIntegrationTests
{
    [Fact]
    public async Task WindowsOcr_RecognizesCleanEnglishSubtitleOverDarkBackground()
    {
        var ocr = new WindowsOcrService();
        Assert.True(ocr.Initialize("auto"), "Windows built-in OCR engine should be available on Windows 10/11.");

        using var frame = new Bitmap(920, 120, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(24, 28, 40));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Arial", 28, FontStyle.Bold);
            g.DrawString("We need to find the key before sunrise.", font, Brushes.White, 25, 35);
        }

        using var pre = ImagePreprocessor.PrepareForOcr(frame, OcrFilterMode.Auto);
        string text = await ocr.RecognizeTextAsync(pre, "auto");

        Assert.Contains("find the key", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sunrise", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsOcr_ReconstructsAllCapsSubtitleInReadingOrder()
    {
        var ocr = new WindowsOcrService();
        Assert.True(ocr.Initialize("auto"));

        using var frame = new Bitmap(900, 180, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(18, 16, 30));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using var font = new Font("Consolas", 26, FontStyle.Bold);
            g.DrawString("PLEASE GIVE IT A GIFT.", font, Brushes.White, 40, 30);
            g.DrawString("KINDNESS", font, Brushes.Yellow, 40, 110);
        }

        using var pre = ImagePreprocessor.PrepareForOcr(frame, OcrFilterMode.Auto);
        string text = await ocr.RecognizeTextAsync(pre, "auto");

        int idxPlease = text.IndexOf("please", StringComparison.OrdinalIgnoreCase);
        int idxKindness = text.IndexOf("kindness", StringComparison.OrdinalIgnoreCase);
        Assert.True(idxPlease >= 0, $"Expected 'please' in OCR output: '{text}'");
        Assert.True(idxKindness > idxPlease, $"Expected 'kindness' after 'please' in reading order: '{text}'");
    }

    [Fact]
    public async Task TranslationService_GamingGlossaryResolvesInZeroMsWithoutNetwork()
    {
        using var translator = new TranslationService();
        var settings = new AppSettings
        {
            Provider = TranslatorProvider.HybridSmart,
            UseGamingGlossary = true,
            CompactMenuLines = true
        };

        var result = await translator.TranslateAsync(
            "Party\nSave\nBack\nKindness\nFear",
            settings,
            CancellationToken.None);

        Assert.True(result.FromCache);
        Assert.Equal("Grupo  •  Salvar  •  Voltar  •  Bondade  •  Medo", result.TranslatedText);
    }

    [Fact]
    public async Task TranslationService_HybridOnlineTranslationAndCacheHit()
    {
        using var translator = new TranslationService();
        var settings = new AppSettings
        {
            Provider = TranslatorProvider.HybridSmart,
            SourceLanguage = "en",
            TargetLanguage = "pt-BR"
        };

        var first = await translator.TranslateAsync(
            "We need to find the key before sunrise.",
            settings,
            CancellationToken.None);

        Assert.False(first.FromCache);
        Assert.False(string.IsNullOrWhiteSpace(first.TranslatedText));
        Assert.Contains("chave", first.TranslatedText, StringComparison.OrdinalIgnoreCase);

        var second = await translator.TranslateAsync(
            "We need to find the key before sunrise.",
            settings,
            CancellationToken.None);

        Assert.True(second.FromCache);
        Assert.Equal(first.TranslatedText, second.TranslatedText);
    }
}
