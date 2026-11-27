using System.IO;
using System.Text.Json;
using GameTranslate.Models;
using GameTranslate.Services;
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

    [Fact]
    public void Runtime_IsNet10OrHigher()
    {
        Assert.True(Environment.Version.Major >= 10, $"Expected .NET 10+, got {Environment.Version}");
    }

    [Fact]
    public void SettingsService_DpapiProtectsAndRestoresSecrets()
    {
        const string secret = "deepl-secret-key-12345:fx";
        string encrypted = SettingsService.ProtectSecret(secret);

        Assert.StartsWith("dpapi:", encrypted);
        Assert.DoesNotContain(secret, encrypted);
        Assert.Equal(secret, SettingsService.UnprotectSecret(encrypted));
        // Backward compatibility with legacy plaintext config values
        Assert.Equal("legacy-plain-key", SettingsService.UnprotectSecret("legacy-plain-key"));
    }

    [Fact]
    public void SettingsService_SaveAndLoad_EncryptsApiKeysOnDisk()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"gt_settings_test_{Guid.NewGuid():N}.json");
        try
        {
            var svc = new SettingsService(tempFile);
            var settings = new AppSettings
            {
                DeepLApiKey = "test-deepl-key-xyz:fx",
                OpenAiApiKey = "sk-test-openai-secret-999"
            };

            svc.Save(settings);

            string rawJson = File.ReadAllText(tempFile);
            Assert.DoesNotContain("test-deepl-key-xyz:fx", rawJson);
            Assert.DoesNotContain("sk-test-openai-secret-999", rawJson);
            Assert.Contains("dpapi:", rawJson);

            AppSettings reloaded = svc.Load();
            Assert.Equal("test-deepl-key-xyz:fx", reloaded.DeepLApiKey);
            Assert.Equal("sk-test-openai-secret-999", reloaded.OpenAiApiKey);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
