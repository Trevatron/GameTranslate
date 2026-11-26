using System.Text.Json;
using GameTranslate.Models;
using Xunit;

namespace GameTranslate.Tests;

public sealed class SettingsAndModelsTests
{
    [Fact]
    public void AppSettings_HasExpectedCinemaYellowAndHybridDefaults()
    {
        var s = new AppSettings();

        Assert.Equal(OverlayPositionMode.BottomOfScreen, s.OverlayPosition);
        Assert.Equal(TranslatorProvider.HybridSmart, s.Provider);
        Assert.Equal("#FFD800", s.SubtitleColorHex);
        Assert.Equal("#000000", s.OutlineColorHex);
        Assert.True(s.ExcludeOverlayFromCapture);
        Assert.True(s.AutoExcludeWindowTitleBar);
        Assert.True(s.AutoCorrectOcrTypos);
        Assert.True(s.CompactMenuLines);
        Assert.True(s.UseGamingGlossary);
        Assert.Equal(140, s.CaptureIntervalMs);
        Assert.Equal("en", s.SourceLanguage);
        Assert.Equal("pt-BR", s.TargetLanguage);
    }

    [Fact]
    public void ScreenRect_ValidationAndCloningWorkProperly()
    {
        var invalid = new ScreenRect { X = 0, Y = 0, Width = 10, Height = 10 };
        Assert.False(invalid.IsValid);

        var valid = new ScreenRect { X = 100, Y = 800, Width = 1200, Height = 150 };
        Assert.True(valid.IsValid);

        var clone = valid.Clone();
        clone.X = 999;
        Assert.Equal(100, valid.X);
        Assert.Contains("1200x150", valid.ToString());
    }

    [Fact]
    public void CreateDefaultBottomSubtitleRect_SitsCenteredNearBottomOf1080pScreen()
    {
        ScreenRect rect = AppSettings.CreateDefaultBottomSubtitleRect(0, 0, 1920, 1080);

        Assert.True(rect.IsValid);
        Assert.True(rect.X > 100 && rect.X < 350);
        Assert.True(rect.Y > 700);
        Assert.True(rect.Y + rect.Height < 1080);
        Assert.True(rect.Width > 1200);
    }

    [Fact]
    public void AppSettings_JsonRoundTrip_PreservesAllFields()
    {
        var original = new AppSettings
        {
            CaptureRegion = new ScreenRect { X = 320, Y = 100, Width = 1280, Height = 720 },
            OverlayPosition = OverlayPositionMode.BottomOfCaptureRegion,
            Provider = TranslatorProvider.BingFree,
            FontSize = 36,
            OutlineThickness = 5.5,
            BackdropOpacity = 0.85,
            CaptureIntervalMs = 120,
            UseGamingGlossary = false
        };

        string json = JsonSerializer.Serialize(original);
        AppSettings? restored = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(320, restored!.CaptureRegion.X);
        Assert.Equal(1280, restored.CaptureRegion.Width);
        Assert.Equal(OverlayPositionMode.BottomOfCaptureRegion, restored.OverlayPosition);
        Assert.Equal(TranslatorProvider.BingFree, restored.Provider);
        Assert.Equal(36, restored.FontSize);
        Assert.Equal(5.5, restored.OutlineThickness);
        Assert.Equal(0.85, restored.BackdropOpacity);
        Assert.Equal(120, restored.CaptureIntervalMs);
        Assert.False(restored.UseGamingGlossary);
    }
}
