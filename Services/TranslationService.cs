using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameTranslate.Models;

namespace GameTranslate.Services;

public sealed record TranslationResult(string TranslatedText, bool FromCache, string ProviderUsed, string? CorrectedSource = null);

public sealed class TranslationService : IDisposable
{
    private const int MaxCacheEntries = 1500;
    private static readonly Regex WordTokenRegex = new(@"[A-Za-zÀ-ÿ]+", RegexOptions.Compiled);

    // Instant 0ms gaming UI/RPG glossary for standalone short menu/HUD labels
    // where generic translators often pick awkward literal meanings (e.g. "Party" -> "Festa", "Back" -> "Costas").
    private static readonly Dictionary<string, string> GamingGlossaryPtBr = new(StringComparer.OrdinalIgnoreCase)
    {
        ["party"] = "Grupo",
        ["save"] = "Salvar",
        ["load"] = "Carregar",
        ["back"] = "Voltar",
        ["resume"] = "Continuar",
        ["continue"] = "Continuar",
        ["quit"] = "Sair",
        ["exit"] = "Sair",
        ["new game"] = "Novo Jogo",
        ["load game"] = "Carregar Jogo",
        ["options"] = "Opções",
        ["settings"] = "Configurações",
        ["controls"] = "Controles",
        ["audio"] = "Áudio",
        ["video"] = "Vídeo",
        ["display"] = "Tela",
        ["graphics"] = "Gráficos",
        ["language"] = "Idioma",
        ["skill"] = "Habilidade",
        ["skills"] = "Habilidades",
        ["ability"] = "Habilidade",
        ["abilities"] = "Habilidades",
        ["item"] = "Item",
        ["items"] = "Itens",
        ["inventory"] = "Inventário",
        ["equip"] = "Equipar",
        ["equipment"] = "Equipamento",
        ["weapon"] = "Arma",
        ["weapons"] = "Armas",
        ["armor"] = "Armadura",
        ["quest"] = "Missão",
        ["quests"] = "Missões",
        ["craft"] = "Criar",
        ["crafting"] = "Criação",
        ["attack"] = "Atacar",
        ["fight"] = "Lutar",
        ["defend"] = "Defender",
        ["guard"] = "Defender",
        ["act"] = "Agir",
        ["magic"] = "Magia",
        ["spell"] = "Feitiço",
        ["spells"] = "Feitiços",
        ["mercy"] = "Misericórdia",
        ["spare"] = "Poupar",
        ["flee"] = "Fugir",
        ["run"] = "Correr",
        ["check"] = "Checar",
        ["stats"] = "Atributos",
        ["status"] = "Status",
        ["map"] = "Mapa",
        ["journal"] = "Diário",
        ["vessel"] = "Receptáculo",
        ["kindness"] = "Bondade",
        ["bravery"] = "Bravura",
        ["ambition"] = "Ambição",
        ["mind"] = "Mente",
        ["voice"] = "Voz",
        ["love"] = "Amor",
        ["hope"] = "Esperança",
        ["disgust"] = "Nojo",
        ["fear"] = "Medo",
        ["yes"] = "Sim",
        ["no"] = "Não",
        ["accept"] = "Aceitar",
        ["cancel"] = "Cancelar",
        ["confirm"] = "Confirmar",
        ["skip"] = "Pular",
        ["retry"] = "Tentar Novamente",
        ["game over"] = "Fim de Jogo",
        ["press any key"] = "Pressione qualquer tecla",
        ["press start"] = "Pressione Start"
    };

    private readonly HttpClient _http;
    private readonly HttpClient _bingHttp;
    private readonly CookieContainer _bingCookies = new();
    private readonly SemaphoreSlim _bingAuthLock = new(1, 1);

    private string _bingIg = string.Empty;
    private string _bingIid = "translator.5023";
    private string _bingKey = string.Empty;
    private string _bingToken = string.Empty;
    private DateTime _bingTokenFetchedUtc = DateTime.MinValue;
    private DateTime _googleGtxCooldownUntilUtc = DateTime.MinValue;
    private int _bingCounter = 1;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();
    private readonly object _cacheLock = new();

    public string LastHybridDiagnostics { get; private set; } = string.Empty;

    public TranslationService()
    {
        var sharedHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(sharedHandler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        var bingHandler = new HttpClientHandler
        {
            CookieContainer = _bingCookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        _bingHttp = new HttpClient(bingHandler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _bingHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    }

    /// <summary>
    /// Pre-warms BOTH Google ChromeEx and Bing LLM TLS connections + auth token in the background
    /// so the very first subtitle translates in ~150-250ms with zero cold-start lag.
    /// </summary>
    public void PrewarmInBackground()
    {
        _ = Task.Run(async () =>
        {
            var t1 = Task.Run(async () =>
            {
                try { await TranslateWithGoogleFastAsync("hello", "en", "pt-BR", CancellationToken.None); }
                catch { }
            });
            var t2 = Task.Run(async () =>
            {
                try { await EnsureBingTokenAsync(CancellationToken.None); }
                catch { }
            });
            await Task.WhenAll(t1, t2);
        });
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
        }
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        AppSettings settings,
        CancellationToken ct,
        Action<TranslationResult>? onFastPreview = null)
    {
        string normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new TranslationResult(string.Empty, true, "None");

        string cacheKey = $"{settings.Provider}|{settings.SourceLanguage}|{settings.TargetLanguage}|{settings.CompactMenuLines}|{settings.UseGamingGlossary}|{normalized}";
        if (_cache.TryGetValue(cacheKey, out string? cached))
        {
            return new TranslationResult(cached, true, "Cache");
        }

        // Fast per-line glossary & per-line cache resolution!
        // If ALL lines are already resolved by Glossary or Line Cache -> 0ms instant return!
        string[] lines = normalized.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var resolvedLines = new string?[lines.Length];
        var missingIndices = new List<int>();
        bool targetIsPt = settings.TargetLanguage.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (settings.UseGamingGlossary && targetIsPt && GamingGlossaryPtBr.TryGetValue(line, out string? gloss))
            {
                resolvedLines[i] = gloss;
                continue;
            }

            string lineKey = $"L|{settings.Provider}|{settings.SourceLanguage}|{settings.TargetLanguage}|{line}";
            if (_cache.TryGetValue(lineKey, out string? cachedLine))
            {
                resolvedLines[i] = cachedLine;
                continue;
            }

            missingIndices.Add(i);
        }

        if (missingIndices.Count == 0)
        {
            string instantMerged = FormatSubtitleOutput(string.Join("\n", resolvedLines!), settings.CompactMenuLines);
            StoreInCache(cacheKey, instantMerged);
            return new TranslationResult(instantMerged, true, "Glossário/Cache");
        }

        // Only send the unresolved lines over the network (keeps menu cursor changes near-0ms!)
        string textToTranslate = string.Join("\n", missingIndices.Select(idx => lines[idx]));

        TranslationResult networkResult;
        switch (settings.Provider)
        {
            case TranslatorProvider.HybridSmart:
                networkResult = await TranslateHybridEnsembleAsync(
                    textToTranslate,
                    settings,
                    ct,
                    partial =>
                    {
                        if (onFastPreview == null) return;
                        string assembled = MergeResolvedLines(lines.Length, resolvedLines, missingIndices, partial.TranslatedText, settings.CompactMenuLines);
                        if (!string.IsNullOrWhiteSpace(assembled))
                            onFastPreview(partial with { TranslatedText = assembled });
                    });
                break;

            case TranslatorProvider.ReversoFree:
                var (revTx, revCorr) = await TranslateWithReversoAsync(textToTranslate, settings.SourceLanguage, settings.TargetLanguage, ct);
                networkResult = new TranslationResult(revTx, false, "Reverso NMT", revCorr);
                break;

            case TranslatorProvider.BingFree:
                string bingTx = await TranslateWithBingAsync(textToTranslate, settings.SourceLanguage, settings.TargetLanguage, ct);
                networkResult = new TranslationResult(bingTx, false, "Bing LLM");
                break;

            case TranslatorProvider.DeepLApi when !string.IsNullOrWhiteSpace(settings.DeepLApiKey):
                networkResult = new TranslationResult(await TranslateWithDeepLAsync(textToTranslate, settings, ct), false, "DeepL");
                break;

            case TranslatorProvider.OpenAiCompatible when !string.IsNullOrWhiteSpace(settings.OpenAiEndpoint):
                networkResult = new TranslationResult(await TranslateWithOpenAiAsync(textToTranslate, settings, ct), false, "LLM");
                break;

            case TranslatorProvider.MyMemoryFree:
                networkResult = new TranslationResult(await TranslateWithMyMemoryAsync(textToTranslate, settings.SourceLanguage, settings.TargetLanguage, ct), false, "MyMemory");
                break;

            case TranslatorProvider.GoogleFree:
            default:
                networkResult = await TranslateGoogleWithAutoCorrectAsync(textToTranslate, settings, ct);
                break;
        }

        // Store individual line translations in line-cache when line count matches cleanly
        string[] translatedChunks = networkResult.TranslatedText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (translatedChunks.Length == missingIndices.Count)
        {
            for (int k = 0; k < missingIndices.Count; k++)
            {
                int origIdx = missingIndices[k];
                string lineKey = $"L|{settings.Provider}|{settings.SourceLanguage}|{settings.TargetLanguage}|{lines[origIdx]}";
                StoreInCache(lineKey, PolishLineCapitalization(translatedChunks[k]));
            }
        }

        string finalFormatted = MergeResolvedLines(lines.Length, resolvedLines, missingIndices, networkResult.TranslatedText, settings.CompactMenuLines);
        if (!string.IsNullOrWhiteSpace(finalFormatted))
        {
            StoreInCache(cacheKey, finalFormatted);
        }

        return networkResult with { TranslatedText = finalFormatted };
    }

    /// <summary>
    /// Smart Hybrid Multi-Engine Ensemble:
    /// Fires Google GTX (ultra-fast + OCR typo autocorrection), Reverso Neural MT (natural phrasing + spellcheck),
    /// and Microsoft Bing LLM (when warm) IN PARALLEL.
    /// Immediately emits the fastest clean result via onFastPreview (~120-200ms) for zero perceived delay,
    /// then silently picks the highest-quality consensus translation once parallel partners arrive (capped at 450ms).
    /// </summary>
    private async Task<TranslationResult> TranslateHybridEnsembleAsync(
        string text,
        AppSettings settings,
        CancellationToken ct,
        Action<TranslationResult>? onFastPreview)
    {
        bool isShortSingleWord = !text.Contains('\n') && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2;

        var errors = new ConcurrentBag<string>();
        // 1. Launch ultra-fast Google ChromeEx (~120-200ms warm, no 429 delay!) and Bing LLM (~220-350ms warm) IN PARALLEL!
        Task<Candidate?> googleFastTask = RunSafeAsync("GoogleFast", errors, async token =>
        {
            string tx = await TranslateWithGoogleFastAsync(text, settings.SourceLanguage, settings.TargetLanguage, token);
            return new Candidate("Google", tx, null, Priority: 1.0);
        }, ct);

        // Also race Bing LLM in parallel for natural PT-BR conversational dialogue (gender agreement, idioms)
        Task<Candidate?>? bingTask = null;
        if (!isShortSingleWord)
        {
            bingTask = RunSafeAsync("Bing", errors, async token =>
            {
                string tx = await TranslateWithBingAsync(text, settings.SourceLanguage, settings.TargetLanguage, token);
                return new Candidate("Bing LLM", tx, null, Priority: 1.35);
            }, ct);
        }

        // Wait for whichever engine finishes first (usually GoogleFast in ~120-200ms!)
        var fastTasks = new List<Task<Candidate?>> { googleFastTask };
        if (bingTask != null) fastTasks.Add(bingTask);

        Candidate? firstGood = null;
        var remaining = new List<Task<Candidate?>>(fastTasks);

        while (remaining.Count > 0 && firstGood == null)
        {
            Task<Candidate?> finished = await Task.WhenAny(remaining);
            remaining.Remove(finished);
            Candidate? cand = await finished;
            if (cand != null && !string.IsNullOrWhiteSpace(cand.Text))
            {
                firstGood = cand;
            }
        }

        if (firstGood == null)
        {
            // Fallback to Google GTX or MyMemory if primary race failed
            try
            {
                var (gtxTx, _) = await TranslateWithGoogleAsync(text, settings.SourceLanguage, settings.TargetLanguage, ct);
                return new TranslationResult(gtxTx, false, "Google (fallback)");
            }
            catch
            {
                string fallback = await TranslateWithMyMemoryAsync(text, settings.SourceLanguage, settings.TargetLanguage, ct);
                return new TranslationResult(fallback, false, "MyMemory (fallback)");
            }
        }

        // Emit fast preview IMMEDIATELY so the user feels zero lag!
        onFastPreview?.Invoke(new TranslationResult(firstGood.Text, false, $"{firstGood.Provider} (rápido)", firstGood.CorrectedSource));

        // Give Bing LLM a tight 380ms window to finish refining (never stalls gameplay!)
        if (remaining.Count > 0 && !ct.IsCancellationRequested)
        {
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waitCts.CancelAfter(380);
                await Task.WhenAll(remaining).WaitAsync(waitCts.Token);
            }
            catch
            {
                // Timeout reached -> keep the instant fast preview cleanly
            }
        }

        var completed = new List<Candidate>();
        foreach (var t in fastTasks)
        {
            if (t.IsCompletedSuccessfully && t.Result != null && !string.IsNullOrWhiteSpace(t.Result.Text))
                completed.Add(t.Result);
        }

        // Only invoke Google GTX spellcheck (dt=qca) if the winning candidate still left untranslated words
        // (avoids 429 delays on clean text while still rescuing garbled OCR!)
        Candidate prelimWinner = SelectBestCandidate(text, completed, isShortSingleWord);
        if (settings.AutoCorrectOcrTypos &&
            DateTime.UtcNow >= _googleGtxCooldownUntilUtc &&
            HasSuspiciousUntranslatedWords(text, prelimWinner.Text))
        {
            try
            {
                var (gtxTx, suggestion) = await TranslateWithGoogleAsync(text, settings.SourceLanguage, settings.TargetLanguage, ct);
                if (!string.IsNullOrWhiteSpace(suggestion) &&
                    !string.Equals(suggestion.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    string fixedTx = await TranslateWithGoogleFastAsync(suggestion.Trim(), settings.SourceLanguage, settings.TargetLanguage, ct);
                    if (!string.IsNullOrWhiteSpace(fixedTx))
                    {
                        LastHybridDiagnostics = string.Join(" | ", errors);
                        return new TranslationResult(fixedTx, false, "Híbrido (corrigido)", suggestion.Trim());
                    }
                }
                else if (!string.IsNullOrWhiteSpace(gtxTx))
                {
                    completed.Add(new Candidate("Google GTX", gtxTx, null, 1.05));
                    prelimWinner = SelectBestCandidate(text, completed, isShortSingleWord);
                }
            }
            catch { }
        }

        LastHybridDiagnostics = string.Join(" | ", errors);
        string providerLabel = completed.Count > 1
            ? $"Híbrido ({prelimWinner.Provider} / {completed.Count} motores)"
            : prelimWinner.Provider;

        return new TranslationResult(prelimWinner.Text, false, providerLabel, prelimWinner.CorrectedSource);
    }

    private static bool HasSuspiciousUntranslatedWords(string sourceEn, string translatedPt)
    {
        var srcWords = new HashSet<string>(
            WordTokenRegex.Matches(sourceEn).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4),
            StringComparer.OrdinalIgnoreCase);
        if (srcWords.Count == 0) return false;

        var outWords = WordTokenRegex.Matches(translatedPt).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4);
        return outWords.Any(w => srcWords.Contains(w) && !IsCommonCognateOrGameTerm(w));
    }

    private sealed record Candidate(string Provider, string Text, string? CorrectedSource, double Priority);

    /// <summary>
    /// Scores multiple PT-BR translation candidates to pick the smartest, most natural game subtitle:
    /// - Penalizes untranslated English words left in the output
    /// - Rewards cross-engine consensus (when 2+ neural engines agree)
    /// - Rewards natural PT-BR conversational engines (Bing LLM / Reverso) on multi-word dialogue
    /// - Rewards proper line-count preservation.
    /// </summary>
    private static Candidate SelectBestCandidate(string sourceEn, List<Candidate> candidates, bool isShortPhrase)
    {
        if (candidates.Count == 1)
            return candidates[0];

        var srcWords = new HashSet<string>(
            WordTokenRegex.Matches(sourceEn).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4),
            StringComparer.OrdinalIgnoreCase);
        int srcLineCount = sourceEn.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        Candidate best = candidates[0];
        double bestScore = double.NegativeInfinity;

        foreach (var c in candidates)
        {
            double score = 0;

            // 1. Penalize untranslated English words copied verbatim into PT-BR
            var outWords = WordTokenRegex.Matches(c.Text).Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length >= 4).ToList();
            int untranslatedCount = outWords.Count(w => srcWords.Contains(w) && !IsCommonCognateOrGameTerm(w));
            score -= untranslatedCount * 4.0;

            // 2. Preserve line structure (critical when translating dialogue + menu list)
            int outLines = c.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            if (outLines == srcLineCount)
                score += 2.0;
            else
                score -= Math.Abs(outLines - srcLineCount) * 1.2;

            // 3. Cross-engine consensus bonus: if another independent engine agrees closely, high confidence!
            foreach (var other in candidates)
            {
                if (ReferenceEquals(c, other)) continue;
                double sim = SubtitlePipeline.ComputeNormalizedSimilarity(c.Text, other.Text);
                if (sim >= 0.72)
                    score += 3.0 * sim;
            }

            // 4. Conversational nuance bonus on full sentences (Bing LLM & Reverso handle PT-BR gender/idioms better)
            if (!isShortPhrase)
                score += c.Priority;
            else
                score += (c.Provider == "Google" ? 1.1 : 1.0);

            // 5. Penalize weird empty/truncated outputs
            if (c.Text.Length < Math.Max(2, sourceEn.Length * 0.35))
                score -= 5.0;

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static bool IsCommonCognateOrGameTerm(string word)
    {
        return word is "menu" or "item" or "status" or "level" or "boss" or "hp" or "mp" or "xp"
            or "start" or "ok" or "normal" or "total" or "final" or "real" or "radio" or "video"
            or "audio" or "portal" or "hotel" or "animal" or "metal" or "crystal" or "normal";
    }

    private static async Task<Candidate?> RunSafeAsync(string name, ConcurrentBag<string> errors, Func<CancellationToken, Task<Candidate?>> work, CancellationToken ct)
    {
        try
        {
            return await work(ct);
        }
        catch (Exception ex)
        {
            errors.Add($"{name}: {ex.Message}");
            return null;
        }
    }

    private static string MergeResolvedLines(
        int totalLines,
        string?[] preResolved,
        List<int> missingIndices,
        string networkTranslated,
        bool compactMenuLines)
    {
        string[] chunks = networkTranslated
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var finalLines = new List<string>(totalLines);

        if (chunks.Length == missingIndices.Count)
        {
            int m = 0;
            for (int i = 0; i < totalLines; i++)
            {
                string line = preResolved[i] ?? chunks[m++];
                finalLines.Add(PolishLineCapitalization(line));
            }
        }
        else
        {
            // Fallback if translator merged or split lines differently: keep preResolved items + translated block cleanly
            bool insertedNetwork = false;
            for (int i = 0; i < totalLines; i++)
            {
                if (preResolved[i] != null)
                {
                    finalLines.Add(PolishLineCapitalization(preResolved[i]!));
                }
                else if (!insertedNetwork)
                {
                    insertedNetwork = true;
                    foreach (string c in chunks)
                        finalLines.Add(PolishLineCapitalization(c));
                }
            }
        }

        return FormatSubtitleOutput(string.Join("\n", finalLines), compactMenuLines);
    }

    private static string PolishLineCapitalization(string line)
    {
        string s = line.Trim();
        if (string.IsNullOrEmpty(s)) return s;

        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsLetter(s[i]))
            {
                if (char.IsLower(s[i]))
                    return s[..i] + char.ToUpperInvariant(s[i]) + s[(i + 1)..];
                break;
            }
        }
        return s;
    }

    private async Task<TranslationResult> TranslateGoogleWithAutoCorrectAsync(string text, AppSettings settings, CancellationToken ct)
    {
        try
        {
            var (tx, suggestion) = await TranslateWithGoogleAsync(text, settings.SourceLanguage, settings.TargetLanguage, ct);
            string translated = tx;
            string providerUsed = "Google";
            string? correctedSource = null;

            if (settings.AutoCorrectOcrTypos &&
                !string.IsNullOrWhiteSpace(suggestion) &&
                !string.Equals(suggestion.Trim(), text, StringComparison.OrdinalIgnoreCase))
            {
                correctedSource = suggestion.Trim();
                var (txFixed, _) = await TranslateWithGoogleAsync(correctedSource, settings.SourceLanguage, settings.TargetLanguage, ct);
                if (!string.IsNullOrWhiteSpace(txFixed))
                {
                    translated = txFixed;
                    providerUsed = "Google (corrigido)";
                }
            }

            return new TranslationResult(translated, false, providerUsed, correctedSource);
        }
        catch when (!ct.IsCancellationRequested)
        {
            string fallback = await TranslateWithMyMemoryAsync(text, settings.SourceLanguage, settings.TargetLanguage, ct);
            return new TranslationResult(fallback, false, "MyMemory (fallback)");
        }
    }

    public static string FormatSubtitleOutput(string text, bool compactMenuLines)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string[] rawLines = text
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PolishLineCapitalization)
            .ToArray();

        if (!compactMenuLines || rawLines.Length < 3)
            return string.Join("\n", rawLines);

        var output = new List<string>();
        var shortRun = new List<string>();

        void FlushShortRun()
        {
            if (shortRun.Count == 0) return;
            if (shortRun.Count >= 3)
                output.Add(string.Join("  •  ", shortRun));
            else
                output.AddRange(shortRun);
            shortRun.Clear();
        }

        foreach (string line in rawLines)
        {
            int words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            bool isShortMenuOption = words <= 2 && line.Length <= 22 && !line.EndsWith('.') && !line.EndsWith('!') && !line.EndsWith('?');
            if (isShortMenuOption)
                shortRun.Add(line);
            else
            {
                FlushShortRun();
                output.Add(line);
            }
        }
        FlushShortRun();

        return string.Join("\n", output);
    }

    private void StoreInCache(string key, string value)
    {
        lock (_cacheLock)
        {
            if (_cache.TryAdd(key, value))
            {
                _cacheOrder.Enqueue(key);
                while (_cacheOrder.Count > MaxCacheEntries)
                {
                    string oldest = _cacheOrder.Dequeue();
                    _cache.TryRemove(oldest, out _);
                }
            }
            else
            {
                _cache[key] = value;
            }
        }
    }

    private async Task<(string Translated, string? SpellCorrection)> TranslateWithGoogleAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        string sl = string.IsNullOrWhiteSpace(sourceLang) ? "auto" : sourceLang;
        string tl = targetLang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt" : targetLang;

        // Try primary Google GTX (supports dt=qca OCR typo correction) unless temporarily rate-limited (429)
        if (DateTime.UtcNow >= _googleGtxCooldownUntilUtc)
        {
            try
            {
                string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(sl)}&tl={Uri.EscapeDataString(tl)}&dt=t&dt=qca&q={Uri.EscapeDataString(text)}";
                using HttpResponseMessage response = await _http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _googleGtxCooldownUntilUtc = DateTime.UtcNow.AddSeconds(90);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync(ct);
                    using JsonDocument doc = JsonDocument.Parse(json);

                    var sb = new StringBuilder();
                    if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                        doc.RootElement.GetArrayLength() > 0 &&
                        doc.RootElement[0].ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement segment in doc.RootElement[0].EnumerateArray())
                        {
                            if (segment.ValueKind == JsonValueKind.Array &&
                                segment.GetArrayLength() > 0 &&
                                segment[0].ValueKind == JsonValueKind.String)
                            {
                                sb.Append(segment[0].GetString());
                            }
                        }
                    }

                    string? spellCorrection = null;
                    if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                        doc.RootElement.GetArrayLength() > 7 &&
                        doc.RootElement[7].ValueKind == JsonValueKind.Array &&
                        doc.RootElement[7].GetArrayLength() > 1 &&
                        doc.RootElement[7][1].ValueKind == JsonValueKind.String)
                    {
                        spellCorrection = doc.RootElement[7][1].GetString();
                    }

                    string result = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(result))
                        return (result, spellCorrection);
                }
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Fall through to secondary Google clients5 endpoint seamlessly
            }
        }

        // Secondary ultra-fast Google Chrome Extension NMT endpoint (separate rate-limit pool!)
        string result2 = await TranslateWithGoogleFastAsync(text, sourceLang, targetLang, ct);
        return (result2, null);
    }

    private async Task<string> TranslateWithGoogleFastAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        string sl = string.IsNullOrWhiteSpace(sourceLang) ? "auto" : sourceLang;
        string tl2 = targetLang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : targetLang;
        string url2 = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={Uri.EscapeDataString(sl)}&tl={Uri.EscapeDataString(tl2)}&q={Uri.EscapeDataString(text)}";
        using HttpResponseMessage resp2 = await _http.GetAsync(url2, ct);
        resp2.EnsureSuccessStatusCode();

        string json2 = await resp2.Content.ReadAsStringAsync(ct);
        using JsonDocument doc2 = JsonDocument.Parse(json2);

        string result2 = string.Empty;
        if (doc2.RootElement.ValueKind == JsonValueKind.Array && doc2.RootElement.GetArrayLength() > 0)
        {
            JsonElement first = doc2.RootElement[0];
            if (first.ValueKind == JsonValueKind.String)
                result2 = first.GetString()?.Trim() ?? string.Empty;
            else if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() > 0 && first[0].ValueKind == JsonValueKind.String)
                result2 = first[0].GetString()?.Trim() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(result2))
            throw new InvalidOperationException("Resposta vazia do Google Fast.");

        return result2;
    }

    private async Task<(string Translated, string? CorrectedText)> TranslateWithReversoAsync(
        string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        string from = MapToReversoLang(sourceLang);
        string to = MapToReversoLang(targetLang);

        var payload = new
        {
            format = "text",
            from,
            to,
            input = text,
            options = new
            {
                sentenceSplitter = true,
                origin = "translation.web",
                contextResults = false,
                languageDetection = false
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.reverso.net/translate/v1/translation")
        {
            Version = HttpVersion.Version11
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.reverso.net");
        req.Headers.TryAddWithoutValidation("Referer", "https://www.reverso.net/");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("translation", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    sb.Append(item.GetString());
            }
        }

        string? corrected = null;
        if (doc.RootElement.TryGetProperty("correctedText", out JsonElement corrEl) && corrEl.ValueKind == JsonValueKind.String)
        {
            corrected = corrEl.GetString();
        }

        string result = sb.ToString().Trim();
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException("Resposta vazia do Reverso.");

        return (result, corrected);
    }

    private static string MapToReversoLang(string code)
    {
        string lower = (code ?? "en").ToLowerInvariant();
        if (lower.StartsWith("pt")) return "por";
        if (lower.StartsWith("es")) return "spa";
        if (lower.StartsWith("fr")) return "fra";
        if (lower.StartsWith("de")) return "ger";
        if (lower.StartsWith("ja")) return "jpn";
        if (lower.StartsWith("it")) return "ita";
        return "eng";
    }

    private async Task EnsureBingTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_bingToken) && (DateTime.UtcNow - _bingTokenFetchedUtc).TotalMinutes < 8)
            return;

        await _bingAuthLock.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(_bingToken) && (DateTime.UtcNow - _bingTokenFetchedUtc).TotalMinutes < 8)
                return;

            string html = await _bingHttp.GetStringAsync("https://www.bing.com/translator", ct);
            Match mIg = Regex.Match(html, @"IG:""([^""]+)""");
            Match mIid = Regex.Match(html, @"data-iid=""([^""]+)""");
            Match mTok = Regex.Match(html, @"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)""");

            if (!mTok.Success)
                throw new InvalidOperationException("Não foi possível obter token do Bing.");

            _bingIg = mIg.Success ? mIg.Groups[1].Value : string.Empty;
            _bingIid = mIid.Success ? mIid.Groups[1].Value : "translator.5023";
            _bingKey = mTok.Groups[1].Value;
            _bingToken = mTok.Groups[2].Value;
            _bingTokenFetchedUtc = DateTime.UtcNow;
        }
        finally
        {
            _bingAuthLock.Release();
        }
    }

    private async Task<string> TranslateWithBingAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        await EnsureBingTokenAsync(ct);

        string from = string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto" ? "auto-detect" : sourceLang;
        string to = targetLang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt" : targetLang;

        int counter = Interlocked.Increment(ref _bingCounter);
        string url = $"https://www.bing.com/ttranslatev3?isVertical=1&&IG={Uri.EscapeDataString(_bingIg)}&IID={Uri.EscapeDataString(_bingIid)}.{counter}";

        var form = new Dictionary<string, string>
        {
            ["fromLang"] = from,
            ["text"] = text,
            ["to"] = to,
            ["token"] = _bingToken,
            ["key"] = _bingKey
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using HttpResponseMessage response = await _bingHttp.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Array &&
            doc.RootElement.GetArrayLength() > 0 &&
            doc.RootElement[0].TryGetProperty("translations", out JsonElement tArr) &&
            tArr.ValueKind == JsonValueKind.Array &&
            tArr.GetArrayLength() > 0 &&
            tArr[0].TryGetProperty("text", out JsonElement textEl))
        {
            string? val = textEl.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }

        // Token may have expired -> invalidate for next cycle
        _bingTokenFetchedUtc = DateTime.MinValue;
        throw new InvalidOperationException("Resposta inválida do Bing Tradutor.");
    }

    private async Task<string> TranslateWithMyMemoryAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
    {
        string sl = string.IsNullOrWhiteSpace(sourceLang) || sourceLang == "auto" ? "en" : sourceLang;
        string tl = string.IsNullOrWhiteSpace(targetLang) ? "pt-BR" : targetLang;

        string url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString(sl)}|{Uri.EscapeDataString(tl)}";
        using HttpResponseMessage response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("responseData", out JsonElement rd) &&
            rd.TryGetProperty("translatedText", out JsonElement tt) &&
            tt.ValueKind == JsonValueKind.String)
        {
            string? val = tt.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }

        throw new InvalidOperationException("Resposta inválida do MyMemory.");
    }

    private async Task<string> TranslateWithDeepLAsync(string text, AppSettings settings, CancellationToken ct)
    {
        string key = settings.DeepLApiKey.Trim();
        bool isFreeKey = key.EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
        string endpoint = isFreeKey
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        string target = settings.TargetLanguage.Equals("pt-BR", StringComparison.OrdinalIgnoreCase)
            ? "PT-BR"
            : settings.TargetLanguage.ToUpperInvariant();

        var payload = new Dictionary<string, object>
        {
            ["text"] = new[] { text },
            ["target_lang"] = target
        };

        if (!string.IsNullOrWhiteSpace(settings.SourceLanguage) &&
            !settings.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            payload["source_lang"] = settings.SourceLanguage.ToUpperInvariant();
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", key);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("translations", out JsonElement arr) &&
            arr.ValueKind == JsonValueKind.Array &&
            arr.GetArrayLength() > 0 &&
            arr[0].TryGetProperty("text", out JsonElement textEl))
        {
            return textEl.GetString()?.Trim() ?? string.Empty;
        }

        throw new InvalidOperationException("Resposta inválida da API DeepL.");
    }

    private async Task<string> TranslateWithOpenAiAsync(string text, AppSettings settings, CancellationToken ct)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.OpenAiModel) ? "llama3.2" : settings.OpenAiModel.Trim(),
            temperature = 0.2,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are a video game subtitle translator. Translate the user's English game subtitle naturally into Brazilian Portuguese (PT-BR). Silently fix minor OCR typos (especially from pixel-art fonts). Return ONLY the translated subtitle text, with no quotes or explanations."
                },
                new
                {
                    role = "user",
                    content = text
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, settings.OpenAiEndpoint.Trim());
        if (!string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey.Trim());
        }
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("choices", out JsonElement choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out JsonElement msg) &&
            msg.TryGetProperty("content", out JsonElement content))
        {
            return content.GetString()?.Trim().Trim('"') ?? string.Empty;
        }

        throw new InvalidOperationException("Resposta inválida do endpoint LLM.");
    }

    public void Dispose()
    {
        _http.Dispose();
        _bingHttp.Dispose();
        _bingAuthLock.Dispose();
    }
}
