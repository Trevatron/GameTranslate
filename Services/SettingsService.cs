using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameTranslate.Models;
using GameTranslate.Native;

namespace GameTranslate.Services;

public sealed class SettingsService
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("GameTranslate.ApiKey.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string SettingsPath { get; }

    public SettingsService()
    {
        SettingsPath = ResolveSettingsPath();
    }

    internal SettingsService(string settingsPath)
    {
        SettingsPath = settingsPath;
    }

    internal static string ProtectSecret(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return string.Empty;

        string trimmed = plaintext.Trim();
        if (trimmed.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return trimmed;

        byte[] raw = Encoding.UTF8.GetBytes(trimmed);
        byte[] protectedBytes = ProtectedData.Protect(raw, DpapiEntropy, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(protectedBytes);
    }

    internal static string UnprotectSecret(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return string.Empty;

        string trimmed = stored.Trim();
        if (!trimmed.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return trimmed; // Backward compatibility with legacy plaintext config

        try
        {
            byte[] protectedBytes = Convert.FromBase64String(trimmed[DpapiPrefix.Length..]);
            byte[] raw = ProtectedData.Unprotect(protectedBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch
        {
            // Key was encrypted under another Windows user/machine or corrupted
            return string.Empty;
        }
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

        settings.DeepLApiKey = UnprotectSecret(settings.DeepLApiKey);
        settings.OpenAiApiKey = UnprotectSecret(settings.OpenAiApiKey);

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

            // Serialize a shallow copy so API keys are DPAPI-encrypted on disk while remaining plaintext in memory
            string rawJson = JsonSerializer.Serialize(settings, JsonOptions);
            AppSettings? toPersist = JsonSerializer.Deserialize<AppSettings>(rawJson, JsonOptions);
            if (toPersist is not null)
            {
                toPersist.DeepLApiKey = ProtectSecret(settings.DeepLApiKey);
                toPersist.OpenAiApiKey = ProtectSecret(settings.OpenAiApiKey);
                string protectedJson = JsonSerializer.Serialize(toPersist, JsonOptions);
                File.WriteAllText(SettingsPath, protectedJson);
            }
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
