using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using GameTranslate.Models;

namespace GameTranslate.Services;

public sealed record SubtitleEventArgs(
    string OriginalText,
    string TranslatedText,
    long LatencyMs,
    bool FromCache,
    string ProviderUsed);

public sealed class SubtitlePipeline : IDisposable
{
    private static readonly Regex NonAlphaNumRegex = new(@"[^a-zA-Z0-9À-ÿ]", RegexOptions.Compiled);

    private readonly ScreenCaptureService _captureService;
    private readonly WindowsOcrService _ocrService;
    private readonly TranslationService _translationService;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _inFlightTxCts;
    private readonly object _txLock = new();
    private long _txSequence;
    private bool _isTranslating;
    private string _inFlightText = string.Empty;
    private string? _queuedText;
    private AppSettings? _queuedSettings;

    private Task? _loopTask;
    private byte[]? _lastFingerprint;
    private string _lastRecognizedText = string.Empty;
    private string _lastTranslatedText = string.Empty;
    private int _consecutiveEmptyCount;
    private DateTime _lastSubtitleShownUtc = DateTime.MinValue;
    private volatile bool _forceNextTick;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public string ActiveOcrLanguage => _ocrService.ActiveLanguageDisplay;

    public event Action<SubtitleEventArgs>? SubtitleUpdated;
    public event Action? SubtitleCleared;
    public event Action<string>? StatusMessage;
    public event Action<string>? ErrorOccurred;

    public SubtitlePipeline(
        ScreenCaptureService captureService,
        WindowsOcrService ocrService,
        TranslationService translationService)
    {
        _captureService = captureService;
        _ocrService = ocrService;
        _translationService = translationService;
    }

    public bool InitializeOcr(string preferredLang = "auto")
    {
        return _ocrService.Initialize(preferredLang);
    }

    public void Start(Func<AppSettings> settingsProvider)
    {
        if (IsRunning)
            return;

        var settings = settingsProvider();
        if (!_ocrService.Initialize(settings.PreferredOcrLanguage))
        {
            ErrorOccurred?.Invoke("Nenhum motor de OCR do Windows disponível neste PC.");
            return;
        }

        _lastFingerprint = null;
        _lastRecognizedText = string.Empty;
        _lastTranslatedText = string.Empty;
        _consecutiveEmptyCount = 0;
        _lastSubtitleShownUtc = DateTime.MinValue;
        _forceNextTick = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        StatusMessage?.Invoke($"Ativo — OCR: {_ocrService.ActiveLanguageDisplay}");
        _loopTask = Task.Run(() => RunLoopAsync(settingsProvider, token), token);
    }

    public void Stop()
    {
        if (_cts == null)
            return;

        CancelInFlightTranslation();

        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
        _lastFingerprint = null;
        StatusMessage?.Invoke("Pausado");
    }

    public void RequestImmediateCapture()
    {
        _forceNextTick = true;
    }

    public async Task<(Bitmap? PreprocessedPreview, string OcrText, string TranslatedText, long LatencyMs)> RunSingleDiagnosticCaptureAsync(AppSettings settings, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _ocrService.Initialize(settings.PreferredOcrLanguage);

        using Bitmap raw = _captureService.CaptureRegion(settings.CaptureRegion);
        Bitmap processed = ImagePreprocessor.PrepareForOcr(raw, settings.OcrFilter, settings.WhiteTextLuminanceThreshold);

        string ocrText = await _ocrService.RecognizeTextAsync(processed, settings.PreferredOcrLanguage);
        string translated = string.Empty;

        if (!string.IsNullOrWhiteSpace(ocrText))
        {
            var result = await _translationService.TranslateAsync(
                ocrText,
                settings,
                ct,
                fast =>
                {
                    SubtitleUpdated?.Invoke(new SubtitleEventArgs(ocrText, fast.TranslatedText, sw.ElapsedMilliseconds, fast.FromCache, fast.ProviderUsed));
                });
            translated = result.TranslatedText;
            sw.Stop();
            SubtitleUpdated?.Invoke(new SubtitleEventArgs(ocrText, translated, sw.ElapsedMilliseconds, result.FromCache, result.ProviderUsed));
        }
        else
        {
            sw.Stop();
        }

        return (processed, ocrText, translated, sw.ElapsedMilliseconds);
    }

    private async Task RunLoopAsync(Func<AppSettings> settingsProvider, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var settings = settingsProvider();
            int intervalMs = Math.Clamp(settings.CaptureIntervalMs, 100, 1500);
            var tickSw = Stopwatch.StartNew();

            try
            {
                await ProcessSingleTickAsync(settings, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Erro no pipeline: {ex.Message}");
            }

            if (settings.AutoHideAfterSecondsEnabled &&
                !string.IsNullOrEmpty(_lastTranslatedText) &&
                _lastSubtitleShownUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastSubtitleShownUtc).TotalSeconds >= Math.Max(3, settings.AutoHideAfterSeconds))
            {
                _forceNextTick = true;
            }

            int elapsed = (int)tickSw.ElapsedMilliseconds;
            int waitMs = Math.Max(30, intervalMs - elapsed);
            try
            {
                await Task.Delay(waitMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessSingleTickAsync(AppSettings settings, CancellationToken ct)
    {
        if (!settings.CaptureRegion.IsValid)
            return;

        bool forced = _forceNextTick;
        _forceNextTick = false;

        var sw = Stopwatch.StartNew();
        using Bitmap raw = _captureService.CaptureRegion(settings.CaptureRegion);
        byte[] currentFp = ScreenCaptureService.ComputeFingerprint(raw);

        double diff = ScreenCaptureService.ComputeMeanAbsDiff(_lastFingerprint, currentFp);
        if (!forced && diff < settings.FrameDiffThreshold)
            return;

        _lastFingerprint = currentFp;

        using Bitmap processed = ImagePreprocessor.PrepareForOcr(raw, settings.OcrFilter, settings.WhiteTextLuminanceThreshold);
        string recognized = await _ocrService.RecognizeTextAsync(processed, settings.PreferredOcrLanguage);

        if (string.IsNullOrWhiteSpace(recognized))
        {
            _consecutiveEmptyCount++;
            if (_consecutiveEmptyCount >= Math.Max(1, settings.EmptyFramesBeforeClear) &&
                !string.IsNullOrEmpty(_lastTranslatedText))
            {
                CancelInFlightTranslation();
                _lastRecognizedText = string.Empty;
                _lastTranslatedText = string.Empty;
                _lastSubtitleShownUtc = DateTime.MinValue;
                SubtitleCleared?.Invoke();
            }
            return;
        }

        _consecutiveEmptyCount = 0;

        // Avoid flickering on minor 1-char OCR jitter when the same subtitle stays on screen
        if (!forced && !string.IsNullOrEmpty(_lastRecognizedText))
        {
            double sim = ComputeNormalizedSimilarity(_lastRecognizedText, recognized);
            if (sim >= settings.TextSimilarityThreshold)
            {
                _lastSubtitleShownUtc = DateTime.UtcNow;
                return;
            }
        }

        // Launch translation asynchronously without stalling the capture/OCR loop!
        // If a newer subtitle appears before HTTP finishes, stale translation is automatically cancelled.
        _lastRecognizedText = recognized;
        DispatchTranslationAsync(recognized, settings, sw, ct);
    }

    private void DispatchTranslationAsync(string recognized, AppSettings settings, Stopwatch sw, CancellationToken loopCt)
    {
        CancellationToken txToken;
        long seq;

        lock (_txLock)
        {
            // Anti-thrash for typewriter dialogue: if a translation is currently in-flight and the new OCR text
            // is simply extending the same sentence letter-by-letter, DO NOT cancel the nearly-finished request;
            // queue the latest text so it translates immediately right after the fast preview lands!
            if (_isTranslating && !string.IsNullOrEmpty(_inFlightText))
            {
                bool isTypewriterExtension =
                    recognized.StartsWith(_inFlightText, StringComparison.OrdinalIgnoreCase) ||
                    ComputeNormalizedSimilarity(_inFlightText, recognized) >= 0.55;

                if (isTypewriterExtension)
                {
                    _queuedText = recognized;
                    _queuedSettings = settings;
                    return;
                }
            }

            try { _inFlightTxCts?.Cancel(); } catch { }
            _inFlightTxCts?.Dispose();
            _inFlightTxCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
            txToken = _inFlightTxCts.Token;
            seq = Interlocked.Increment(ref _txSequence);
            _isTranslating = true;
            _inFlightText = recognized;
            _queuedText = null;
            _queuedSettings = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string lastEmitted = string.Empty;

                TranslationResult finalResult = await _translationService.TranslateAsync(
                    recognized,
                    settings,
                    txToken,
                    onFastPreview: fast =>
                    {
                        if (txToken.IsCancellationRequested || seq != Interlocked.Read(ref _txSequence))
                            return;
                        if (string.IsNullOrWhiteSpace(fast.TranslatedText))
                            return;

                        lastEmitted = fast.TranslatedText;
                        _lastTranslatedText = fast.TranslatedText;
                        _lastSubtitleShownUtc = DateTime.UtcNow;

                        SubtitleUpdated?.Invoke(new SubtitleEventArgs(
                            recognized,
                            fast.TranslatedText,
                            sw.ElapsedMilliseconds,
                            fast.FromCache,
                            fast.ProviderUsed));
                    });

                sw.Stop();
                if (txToken.IsCancellationRequested || seq != Interlocked.Read(ref _txSequence))
                    return;

                if (!string.IsNullOrWhiteSpace(finalResult.TranslatedText))
                {
                    _lastTranslatedText = finalResult.TranslatedText;
                    _lastSubtitleShownUtc = DateTime.UtcNow;

                    if (!string.Equals(finalResult.TranslatedText, lastEmitted, StringComparison.Ordinal) || string.IsNullOrEmpty(lastEmitted))
                    {
                        SubtitleUpdated?.Invoke(new SubtitleEventArgs(
                            recognized,
                            finalResult.TranslatedText,
                            sw.ElapsedMilliseconds,
                            finalResult.FromCache,
                            finalResult.ProviderUsed));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Newer frame superseded this translation
            }
            catch (Exception ex)
            {
                if (!txToken.IsCancellationRequested)
                    ErrorOccurred?.Invoke($"Erro na tradução: {ex.Message}");
            }
            finally
            {
                string? nextText = null;
                AppSettings? nextSettings = null;
                lock (_txLock)
                {
                    if (seq == Interlocked.Read(ref _txSequence))
                    {
                        _isTranslating = false;
                        _inFlightText = string.Empty;
                        if (!string.IsNullOrWhiteSpace(_queuedText) && _queuedSettings != null && !loopCt.IsCancellationRequested)
                        {
                            nextText = _queuedText;
                            nextSettings = _queuedSettings;
                            _queuedText = null;
                            _queuedSettings = null;
                        }
                    }
                }

                if (nextText != null && nextSettings != null)
                {
                    DispatchTranslationAsync(nextText, nextSettings, Stopwatch.StartNew(), loopCt);
                }
            }
        }, txToken);
    }

    private void CancelInFlightTranslation()
    {
        lock (_txLock)
        {
            Interlocked.Increment(ref _txSequence);
            _isTranslating = false;
            _inFlightText = string.Empty;
            _queuedText = null;
            _queuedSettings = null;
            try { _inFlightTxCts?.Cancel(); } catch { }
            _inFlightTxCts?.Dispose();
            _inFlightTxCts = null;
        }
    }

    public static double ComputeNormalizedSimilarity(string a, string b)
    {
        string na = NonAlphaNumRegex.Replace(a, "").ToLowerInvariant();
        string nb = NonAlphaNumRegex.Replace(b, "").ToLowerInvariant();

        if (na.Length == 0 && nb.Length == 0) return 1.0;
        if (na.Length == 0 || nb.Length == 0) return 0.0;
        if (na == nb) return 1.0;

        int dist = LevenshteinDistance(na, nb);
        int maxLen = Math.Max(na.Length, nb.Length);
        return 1.0 - ((double)dist / maxLen);
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    public void Dispose()
    {
        Stop();
    }
}
