using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows;
using GameTranslate.Models;
using GameTranslate.Native;
using GameTranslate.Services;
using GameTranslate.Windows;

namespace GameTranslate;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            int imgIdx = Array.FindIndex(e.Args, a => a.Equals("--test-image", StringComparison.OrdinalIgnoreCase));
            string? customImg = (imgIdx >= 0 && imgIdx + 1 < e.Args.Length) ? e.Args[imgIdx + 1] : null;
            int code = await RunSelfTestAsync(customImg);
            Shutdown(code);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "GameTranslate_SingleInstance_Mutex_v1", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "O GameTranslate já está aberto (verifique o ícone na bandeja perto do relógio).",
                "GameTranslate",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                $"Ocorreu um erro inesperado:\n{args.Exception.Message}",
                "GameTranslate",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static async Task<int> RunSelfTestAsync(string? customImagePath = null)
    {
        var report = new List<string>();
        try
        {
            report.Add($"ExcludeFromCapture supported: {Win32.SupportsExcludeFromCapture}");

            var ocr = new WindowsOcrService();
            bool ocrReady = ocr.Initialize("auto");
            report.Add($"OCR initialized: {ocrReady} ({ocr.ActiveLanguageDisplay})");

            using var translator = new TranslationService();
            var settings = new AppSettings();
            // Pre-warm Bing LLM token just like MainWindow does on startup so latency reflects real gameplay
            translator.PrewarmInBackground();
            await Task.Delay(750);

            if (!string.IsNullOrWhiteSpace(customImagePath) && File.Exists(customImagePath))
            {
                using var loaded = new Bitmap(customImagePath);
                // Simulate titlebar trim (top 36px) as Win32.TrimWindowTitleBarIfOverlapping does on live window
                using var clientBmp = loaded.Clone(new Rectangle(0, 36, (int)(loaded.Width * 0.94), loaded.Height - 36), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var pre = ImagePreprocessor.PrepareForOcr(clientBmp, OcrFilterMode.Auto);
                string recognized = await ocr.RecognizeTextAsync(pre, "auto");
                report.Add($"[IMAGE] OCR Raw Reconstructed:\n{recognized}");

                var swImg = System.Diagnostics.Stopwatch.StartNew();
                long firstPreviewMs = -1;
                string firstPreviewProvider = "";
                var tx = await translator.TranslateAsync(
                    recognized,
                    settings,
                    CancellationToken.None,
                    fast =>
                    {
                        if (firstPreviewMs < 0)
                        {
                            firstPreviewMs = swImg.ElapsedMilliseconds;
                            firstPreviewProvider = fast.ProviderUsed;
                        }
                    });
                swImg.Stop();
                if (firstPreviewMs >= 0)
                    report.Add($"[IMAGE] Fast Preview: {firstPreviewMs} ms ({firstPreviewProvider})");
                report.Add($"[IMAGE] Final Provider: {tx.ProviderUsed} (total {swImg.ElapsedMilliseconds} ms)");
                if (!string.IsNullOrWhiteSpace(translator.LastHybridDiagnostics))
                    report.Add($"[IMAGE] Hybrid Diag: {translator.LastHybridDiagnostics}");
                if (!string.IsNullOrWhiteSpace(tx.CorrectedSource))
                    report.Add($"[IMAGE] Auto-corrected EN:\n{tx.CorrectedSource}");
                report.Add($"[IMAGE] Final PT-BR Subtitle:\n{tx.TranslatedText}");
            }

            // 1. Render synthetic game subtitle over colorful 3D-like gradient background
            using var gameFrame = new Bitmap(920, 120);
            using (var g = Graphics.FromImage(gameFrame))
            {
                using var bg = new LinearGradientBrush(
                    new Rectangle(0, 0, 920, 120),
                    Color.FromArgb(25, 55, 45),
                    Color.FromArgb(80, 45, 35),
                    15f);
                g.FillRectangle(bg, 0, 0, 920, 120);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using var font = new Font("Arial", 26, System.Drawing.FontStyle.Bold);
                g.DrawString("Don't open that door! Something is behind it.", font, Brushes.Black, 32, 42);
                g.DrawString("Don't open that door! Something is behind it.", font, Brushes.White, 30, 40);
            }

            // 2. Test ImagePreprocessor + WindowsOcrService (Auto & WhiteYellowText)
            using var preAuto = ImagePreprocessor.PrepareForOcr(gameFrame, OcrFilterMode.Auto);
            string textAuto = await ocr.RecognizeTextAsync(preAuto, "auto");
            report.Add($"OCR [Auto]: {textAuto}");

            using var preWhite = ImagePreprocessor.PrepareForOcr(gameFrame, OcrFilterMode.WhiteYellowText, 175);
            string textWhite = await ocr.RecognizeTextAsync(preWhite, "auto");
            report.Add($"OCR [WhiteYellowText]: {textWhite}");

            // 3. Test TranslationService + LRU Cache
            string sampleForTx = !string.IsNullOrWhiteSpace(textAuto) ? textAuto : textWhite;
            var res1 = await translator.TranslateAsync(sampleForTx, settings, CancellationToken.None);
            var res2 = await translator.TranslateAsync(sampleForTx, settings, CancellationToken.None);
            report.Add($"Translation 1 ({res1.ProviderUsed}, cache={res1.FromCache}): {res1.TranslatedText}");
            report.Add($"Translation 2 ({res2.ProviderUsed}, cache={res2.FromCache}): {res2.TranslatedText}");

            // 4. Verify SubtitleOverlayWindow & WDA_EXCLUDEFROMCAPTURE + BottomOfScreen default
            var overlay = new SubtitleOverlayWindow();
            overlay.Show();
            overlay.ApplySettings(settings);
            overlay.ShowSubtitle(res1.TranslatedText, sampleForTx, showOriginal: false);
            bool stealthOk = overlay.IsExcludedFromCaptureActive;
            var bounds = overlay.GetPhysicalBounds();
            report.Add($"Overlay WDA_EXCLUDEFROMCAPTURE active: {stealthOk}, BottomRect={bounds}");
            overlay.Close();

            // 5. Verify phonotactic pixel-art R->A repair (fixes garbled words, ZERO false positives on normal English)
            string garbled = "Give it r nrme. Whrt is thrt? Rll of us crn trlk rnd srve the drrk world. I ferr nothing. Rmbition Brrvery Clerr";
            string repaired = WindowsOcrService.CleanCommonPixelOcrGlitches(garbled);
            string expectedRepaired = "Give it a name. What is that? All of us can talk and save the dark world. I fear nothing. Ambition Bravery Clear";

            string normalEn = "Three strong sprites scream across the street. Rhythm and rhyme. Dr. Smith cried dry tears. Try Mr. Fox on Ctrl.";
            string normalOut = WindowsOcrService.CleanCommonPixelOcrGlitches(normalEn);
            bool repairOk = repaired == expectedRepaired && normalOut == normalEn;
            report.Add($"Pixel R->A Repair OK: {repairOk} | '{repaired}'");

            bool passed = ocrReady &&
                          !string.IsNullOrWhiteSpace(textAuto) &&
                          !string.IsNullOrWhiteSpace(textWhite) &&
                          !string.IsNullOrWhiteSpace(res1.TranslatedText) &&
                          res2.FromCache &&
                          stealthOk &&
                          repairOk;

            report.Add(passed ? "SELF_TEST_PASSED" : "SELF_TEST_FAILED");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "_selftest_result.txt"), report, System.Text.Encoding.UTF8);
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Add($"EXCEPTION: {ex}");
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "_selftest_result.txt"), report, System.Text.Encoding.UTF8);
            return 2;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
