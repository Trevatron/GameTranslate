using System.IO;
using System.Text.Json;
using GameTranslate.Models;
using GameTranslate.Native;

namespace GameTranslate.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public SettingsService()
    {
        SettingsPath = ResolveSettingsPath();
    }

    private static string ResolveSettingsPath()
    {
        // Prefer portable config right next to the single .exe if writable
        try
        {
            string exeDir = AppContext.BaseDirectory;
            string portablePath = Path.Combine(exeDir, "GameTranslate.json");
            string probePath = Path.Combine(exeDir, ".gt_write_probe");
            File.WriteAllText(probePath, "ok");
            File.Delete(probePath);
            return portablePath;
        }
        catch
        {
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameTranslate");
            Directory.CreateDirectory(appDataDir);
            return Path.Combine(appDataDir, "GameTranslate.json");
        }
    }

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch
        {
            settings = new AppSettings();
        }

        EnsureValidRegions(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore write failures cleanly
        }
    }

    private static void EnsureValidRegions(AppSettings settings)
    {
        var (screenW, screenH) = Win32.GetPrimaryScreenSize();
        if (screenW <= 0) screenW = 1920;
        if (screenH <= 0) screenH = 1080;

        if (!settings.CaptureRegion.IsValid)
        {
            settings.CaptureRegion = AppSettings.CreateDefaultBottomSubtitleRect(0, 0, screenW, screenH);
        }

        if (!settings.OverlayRegion.IsValid)
        {
            settings.OverlayRegion = settings.CaptureRegion.Clone();
        }
    }
}
