using System.Text.Json.Serialization;

namespace GameTranslate.Models;

public enum OcrFilterMode
{
    Auto,
    WhiteYellowText,
    HighContrast,
    Original
}

public enum TranslatorProvider
{
    HybridSmart,
    GoogleFree,
    ReversoFree,
    BingFree,
    MyMemoryFree,
    DeepLApi,
    OpenAiCompatible
}

public enum OverlayPositionMode
{
    /// <summary>Pinned neatly to the bottom of the screen like cinema/TV series subtitles (Default).</summary>
    BottomOfScreen,
    /// <summary>Pinned to the bottom edge of the captured region/game window.</summary>
    BottomOfCaptureRegion,
    /// <summary>Placed directly over the captured subtitle area (uses WDA_EXCLUDEFROMCAPTURE).</summary>
    OverCaptureRegion,
    /// <summary>Freely positioned and resized by the user.</summary>
    CustomFree
}

public sealed class ScreenRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [JsonIgnore]
    public bool IsValid => Width >= 20 && Height >= 16;

    public ScreenRect Clone() => new() { X = X, Y = Y, Width = Width, Height = Height };

    public override string ToString() => $"{Width}x{Height} em ({X}, {Y})";
}

public sealed class AppSettings
{
    // Capture area in physical virtual-screen pixels
    public ScreenRect CaptureRegion { get; set; } = new();

    // Subtitle overlay placement mode (default: Bottom of Screen like TV/movies!)
    public OverlayPositionMode OverlayPosition { get; set; } = OverlayPositionMode.BottomOfScreen;

    // Independent overlay area (physical pixels) used when OverlayPosition == CustomFree
    public ScreenRect OverlayRegion { get; set; } = new();

    // Exclude overlay window from screen capture (Windows 10 2004+ / Windows 11)
    public bool ExcludeOverlayFromCapture { get; set; } = true;

    // Automatically strip Windows titlebar (e.g., "CONTACT") when capturing a game window
    public bool AutoExcludeWindowTitleBar { get; set; } = true;

    // Subtitle appearance (Classic Yellow TV/Cinema style)
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 30;
    public bool FontBold { get; set; } = true;
    public bool FontItalic { get; set; } = false;
    public string SubtitleColorHex { get; set; } = "#FFD800";
    public string OutlineColorHex { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 4.5;
    public bool DropShadowEnabled { get; set; } = true;

    // Backdrop behind yellow subtitle (0.0 = pure floating yellow text, 0.72 = subtle dark bar)
    public double BackdropOpacity { get; set; } = 0.68;
    public double BackdropCornerRadius { get; set; } = 8;

    // Compact stacked short menu items onto one clean bulleted line so subtitles stay at screen bottom
    public bool CompactMenuLines { get; set; } = true;

    // OCR & Capture pipeline tuning
    public int CaptureIntervalMs { get; set; } = 140;
    public OcrFilterMode OcrFilter { get; set; } = OcrFilterMode.Auto;
    public int WhiteTextLuminanceThreshold { get; set; } = 175;
    public double FrameDiffThreshold { get; set; } = 2.8;
    public double TextSimilarityThreshold { get; set; } = 0.88;
    public int EmptyFramesBeforeClear { get; set; } = 2;
    public bool AutoHideAfterSecondsEnabled { get; set; } = true;
    public int AutoHideAfterSeconds { get; set; } = 10;
    public string PreferredOcrLanguage { get; set; } = "auto";
    public bool AutoCorrectOcrTypos { get; set; } = true;
    public bool UseGamingGlossary { get; set; } = true;

    // Translation settings
    public TranslatorProvider Provider { get; set; } = TranslatorProvider.HybridSmart;
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "pt-BR";
    public string DeepLApiKey { get; set; } = string.Empty;
    public string OpenAiEndpoint { get; set; } = "http://localhost:11434/v1/chat/completions";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "llama3.2";

    // App behavior
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowOriginalTextSubtle { get; set; } = false;
    public bool AlwaysKeepOnTopReinforce { get; set; } = true;

    public static ScreenRect CreateDefaultBottomSubtitleRect(int screenX, int screenY, int screenW, int screenH)
    {
        int width = (int)(screenW * 0.78);
        int height = Math.Max(140, (int)(screenH * 0.22));
        int x = screenX + (screenW - width) / 2;
        int y = screenY + screenH - height - (int)(screenH * 0.045);
        return new ScreenRect { X = x, Y = y, Width = width, Height = height };
    }
}
