using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GameTranslate.Services;

public sealed class WindowsOcrService
{
    private static readonly Regex MultiSpaceRegex = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex LetterRegex = new(@"[A-Za-zÀ-ÿ]", RegexOptions.Compiled);

    private OcrEngine? _engine;
    private string _activeLanguageTag = string.Empty;

    public string ActiveLanguageDisplay { get; private set; } = "Não inicializado";

    public static IReadOnlyList<(string Tag, string DisplayName)> GetAvailableLanguages()
    {
        var list = new List<(string, string)>();
        foreach (var lang in OcrEngine.AvailableRecognizerLanguages)
        {
            list.Add((lang.LanguageTag, $"{lang.LanguageTag} — {lang.DisplayName}"));
        }
        return list;
    }

    public bool Initialize(string preferredLanguageTag = "auto")
    {
        var available = OcrEngine.AvailableRecognizerLanguages;
        if (available.Count == 0)
        {
            ActiveLanguageDisplay = "Nenhum pacote OCR do Windows encontrado";
            _engine = null;
            return false;
        }

        Language? chosen = null;

        if (!string.IsNullOrWhiteSpace(preferredLanguageTag) &&
            !preferredLanguageTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            chosen = available.FirstOrDefault(l =>
                l.LanguageTag.Equals(preferredLanguageTag, StringComparison.OrdinalIgnoreCase));
        }

        chosen ??= available.FirstOrDefault(l =>
            l.LanguageTag.Equals("en-US", StringComparison.OrdinalIgnoreCase));
        chosen ??= available.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        chosen ??= available.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("pt", StringComparison.OrdinalIgnoreCase));
        chosen ??= available.FirstOrDefault(l =>
            l.LanguageTag.StartsWith("es", StringComparison.OrdinalIgnoreCase) ||
            l.LanguageTag.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ||
            l.LanguageTag.StartsWith("de", StringComparison.OrdinalIgnoreCase) ||
            l.LanguageTag.StartsWith("it", StringComparison.OrdinalIgnoreCase));
        chosen ??= available[0];

        _engine = OcrEngine.TryCreateFromLanguage(chosen) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (_engine != null)
        {
            _activeLanguageTag = _engine.RecognizerLanguage.LanguageTag;
            ActiveLanguageDisplay = $"{_engine.RecognizerLanguage.LanguageTag} ({_engine.RecognizerLanguage.DisplayName})";
            return true;
        }

        ActiveLanguageDisplay = "Falha ao iniciar Windows OCR";
        return false;
    }

    public async Task<string> RecognizeTextAsync(Bitmap bitmap, string preferredLanguageTag = "auto")
    {
        if (_engine == null ||
            (!preferredLanguageTag.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
             !_activeLanguageTag.Equals(preferredLanguageTag, StringComparison.OrdinalIgnoreCase)))
        {
            if (!Initialize(preferredLanguageTag))
                return string.Empty;
        }

        if (_engine == null)
            return string.Empty;

        using SoftwareBitmap softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        OcrResult result = await _engine.RecognizeAsync(softwareBitmap);

        if (result == null || result.Lines.Count == 0)
            return string.Empty;

        return ReconstructReadingOrderText(result);
    }

    private sealed class WordBox
    {
        public string Text { get; init; } = string.Empty;
        public double X { get; init; }
        public double Y { get; init; }
        public double Width { get; init; }
        public double Height { get; init; }
        public double CenterY => Y + Height * 0.5;
        public double Right => X + Width;
    }

    private sealed class VisualRow
    {
        public List<WordBox> Words { get; } = new();
        public double AvgCenterY => Words.Average(w => w.CenterY);
        public double AvgHeight => Words.Average(w => w.Height);
        public double Top => Words.Min(w => w.Y);
        public double Bottom => Words.Max(w => w.Y + w.Height);
        public double Left => Words.Min(w => w.X);
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>
    /// Reconstructs clean, natural reading order directly from OcrWord bounding boxes.
    /// Fixes Windows OCR returning wide-spaced game/pixel-art words out of order,
    /// re-merges split wide-tracked letters (e.g., "P LE ASE" -> "PLEASE", "g IVE" -> "GIVE"),
    /// discards tiny sprite/titlebar noise, preserves paragraph/menu breaks,
    /// and normalizes ALL-CAPS lines to Sentence case for far better translation quality.
    /// </summary>
    private static string ReconstructReadingOrderText(OcrResult result)
    {
        var allWords = new List<WordBox>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                string t = word.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(t))
                    continue;

                var r = word.BoundingRect;
                if (r.Width < 2 || r.Height < 4)
                    continue;

                allWords.Add(new WordBox
                {
                    Text = t,
                    X = r.X,
                    Y = r.Y,
                    Width = r.Width,
                    Height = r.Height
                });
            }
        }

        if (allWords.Count == 0)
            return string.Empty;

        // Filter out tiny stray noise tokens (e.g., 10px titlebar or sprite pixels when main font is 30px)
        if (allWords.Count >= 3)
        {
            var sortedHeights = allWords.Select(w => w.Height).OrderBy(h => h).ToList();
            double medianH = sortedHeights[sortedHeights.Count / 2];
            double minAllowedH = medianH * 0.48;

            allWords = allWords
                .Where(w => w.Height >= minAllowedH || (LetterRegex.Matches(w.Text).Count >= 3 && w.Height >= medianH * 0.35))
                .ToList();
        }

        if (allWords.Count == 0)
            return string.Empty;

        // Group words into horizontal visual rows based on vertical center alignment
        allWords.Sort((a, b) =>
        {
            int cmpY = a.CenterY.CompareTo(b.CenterY);
            return cmpY != 0 ? cmpY : a.X.CompareTo(b.X);
        });

        var rows = new List<VisualRow>();
        foreach (var word in allWords)
        {
            VisualRow? targetRow = null;
            double bestDist = double.MaxValue;

            foreach (var row in rows)
            {
                double dist = Math.Abs(word.CenterY - row.AvgCenterY);
                double tolerance = Math.Max(word.Height, row.AvgHeight) * 0.58;
                if (dist <= tolerance && dist < bestDist)
                {
                    bestDist = dist;
                    targetRow = row;
                }
            }

            if (targetRow == null)
            {
                targetRow = new VisualRow();
                rows.Add(targetRow);
            }
            targetRow.Words.Add(word);
        }

        // Sort rows top-to-bottom and assemble each row left-to-right with smart wide-tracking merge
        rows.Sort((a, b) => a.Top.CompareTo(b.Top));

        var validRows = new List<VisualRow>();
        foreach (var row in rows)
        {
            row.Words.Sort((a, b) => a.X.CompareTo(b.X));

            // Drop stray purely-numeric/symbol sprite artifacts inside a row if isolated far from text
            var filteredWords = row.Words
                .Where(w => LetterRegex.IsMatch(w.Text) || w.Text.Length >= 2 && row.Words.Any(other => other != w && LetterRegex.IsMatch(other.Text) && Math.Abs(other.X - w.X) < row.AvgHeight * 4))
                .ToList();

            if (filteredWords.Count == 0)
                continue;

            var sb = new StringBuilder();
            WordBox? prev = null;

            // Compute max horizontal gap on this row to distinguish true wide-tracked word spaces
            // (e.g. 56px between words vs 19px between split pixel-art letters) from normal proportional fonts.
            double maxRowGap = 0;
            for (int k = 1; k < filteredWords.Count; k++)
            {
                double g = filteredWords[k].X - filteredWords[k - 1].Right;
                if (g > maxRowGap) maxRowGap = g;
            }

            foreach (var w in filteredWords)
            {
                if (prev != null)
                {
                    double gap = w.X - prev.Right;
                    double refH = Math.Max(prev.Height, w.Height);
                    bool prevEndsPunct = prev.Text.Length > 0 && ".!?,:;".Contains(prev.Text[^1]);

                    string prevLettersOnly = Regex.Replace(prev.Text, @"[^A-Za-zÀ-ÿ]", "");
                    string currLettersOnly = Regex.Replace(w.Text, @"[^A-Za-zÀ-ÿ]", "");

                    // True wide-tracked row has word spaces >= 1.10 * char height (normal fonts have ~0.25-0.40 * height)
                    bool isWideTrackedRow = maxRowGap >= refH * 1.10;
                    bool isWideTrackedFragment = isWideTrackedRow && gap < maxRowGap * 0.56 && gap < refH * 0.92;

                    // Single orphaned non-word letter split at start (e.g. "P"+"ler", "g"+"ive", "R"+"bout")
                    // or end (e.g. "you"+"r", "tio"+"n?", "gif"+"t.", "he"+"r"+"r.")
                    double orphanLimit = isWideTrackedRow ? Math.Min(maxRowGap * 0.56, refH * 0.92) : refH * 0.52;
                    bool isLeadingOrphan = prevLettersOnly.Length == 1 &&
                        !prevLettersOnly.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                        !prevLettersOnly.Equals("i", StringComparison.OrdinalIgnoreCase) &&
                        gap < orphanLimit;
                    bool isTrailingOrphan = currLettersOnly.Length == 1 &&
                        !currLettersOnly.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                        !currLettersOnly.Equals("i", StringComparison.OrdinalIgnoreCase) &&
                        gap < orphanLimit;

                    // Special case: "R gif t." where "R" was misread for standalone "A" -> keep space if gap is a wide word gap
                    if (isLeadingOrphan && prevLettersOnly.Equals("r", StringComparison.OrdinalIgnoreCase) &&
                        isWideTrackedRow && gap >= maxRowGap * 0.60)
                    {
                        isLeadingOrphan = false;
                    }

                    bool isTouching = gap < refH * 0.10;
                    bool isSplitFragment = !prevEndsPunct && (isTouching || isWideTrackedFragment || isLeadingOrphan || isTrailingOrphan);

                    if (!isSplitFragment)
                        sb.Append(' ');
                }

                sb.Append(w.Text);
                prev = w;
            }

            string rowText = MultiSpaceRegex.Replace(sb.ToString(), " ").Trim();
            // Remove stray OCR hyphens inside a single word (e.g. "Kindne-ss" -> "Kindness")
            rowText = Regex.Replace(rowText, @"(?<=[A-Za-z])-(?=[a-z]{1,3}\b)", "");
            if (LetterRegex.Matches(rowText).Count < 2)
                continue;

            row.Text = CleanCommonPixelOcrGlitches(NormalizeAllCapsIfNeeded(rowText));
            validRows.Add(row);
        }

        if (validRows.Count == 0)
            return string.Empty;

        // Join wrapped sentence lines with space; preserve '\n' between separate UI blocks or menu lists
        var output = new StringBuilder();
        for (int i = 0; i < validRows.Count; i++)
        {
            var curr = validRows[i];
            if (i == 0)
            {
                output.Append(curr.Text);
                continue;
            }

            var prev = validRows[i - 1];
            double verticalGap = curr.Top - prev.Bottom;
            double refH = Math.Max(prev.AvgHeight, curr.AvgHeight);

            bool closeVertically = verticalGap <= refH * 1.25;
            bool prevEndsSentence = prev.Text.Length > 0 && ".!?…:".Contains(prev.Text[^1]);
            int prevWordCount = prev.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            int currWordCount = curr.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            // Stacked 1-word menu items (e.g., Kindness / Mind / Ambition / Bravery / Voice) stay on separate lines
            bool looksLikeMenuList = prevWordCount == 1 && currWordCount == 1;

            if (closeVertically && !prevEndsSentence && !looksLikeMenuList)
            {
                if (output.Length > 0 && output[^1] == '-')
                    output.Length -= 1;
                else
                    output.Append(' ');

                // Lowercase continuation word when joining a wrapped ALL-CAPS sentence
                string nextChunk = curr.Text;
                if (nextChunk.Length > 1 && char.IsUpper(nextChunk[0]) && char.IsLower(nextChunk[1]) &&
                    !nextChunk.StartsWith("I ", StringComparison.Ordinal) && nextChunk != "I")
                {
                    nextChunk = char.ToLowerInvariant(nextChunk[0]) + nextChunk[1..];
                }
                output.Append(nextChunk);
            }
            else
            {
                output.Append('\n');
                output.Append(curr.Text);
            }
        }

        string finalStr = output.ToString().Trim();
        return LetterRegex.Matches(finalStr).Count >= 2 ? finalStr : string.Empty;
    }

    /// <summary>
    /// Converts predominantly ALL-CAPS game text (e.g. "PLEASE gIVE IT") into clean Sentence case
    /// ("Please give it"), which dramatically improves machine translation quality and spell-correction.
    /// </summary>
    private static string NormalizeAllCapsIfNeeded(string line)
    {
        int letters = 0;
        int uppers = 0;
        foreach (char c in line)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (char.IsUpper(c)) uppers++;
            }
        }

        if (letters < 3 || (double)uppers / letters < 0.72)
            return line;

        string lower = line.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        bool capitalizeNext = true;

        for (int i = 0; i < lower.Length; i++)
        {
            char c = lower[i];
            if (capitalizeNext && char.IsLetter(c))
            {
                sb.Append(char.ToUpperInvariant(c));
                capitalizeNext = false;
            }
            else
            {
                sb.Append(c);
            }

            if (c == '.' || c == '!' || c == '?')
                capitalizeNext = true;
        }

        // Preserve standalone English pronoun "I"
        return Regex.Replace(sb.ToString(), @"\bi\b", "I");
    }

    private static readonly HashSet<string> ExemptShortTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "dr", "jr", "sr", "st", "brr", "grr", "err", "ctrl", "rpg", "crt", "fps", "npc", "btn", "str", "src", "dst", "ptr"
    };

    /// <summary>
    /// Repairs unambiguous pixel-art / 8-bit OCR glitches — especially flat-top capital 'A' being
    /// misread as 'R' (e.g. standalone 'r' -> 'a', 'rbout' -> 'about', 'thrt' -> 'that', 'nrme' -> 'name',
    /// 'ferr' -> 'fear', 'brrvery' -> 'bravery') — using English phonotactic rules that have zero
    /// false positives on valid English words (e.g. 'street', 'strong', 'world', 'rhythm', 'cry', 'try').
    /// </summary>
    public static string CleanCommonPixelOcrGlitches(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string s = text;

        // 1. Standalone "r" / "R" in English is virtually always pixel-art "a" / "A"
        s = Regex.Replace(s, @"(?<![A-Za-zÀ-ÿ0-9])r(?![A-Za-zÀ-ÿ0-9])", "a");
        s = Regex.Replace(s, @"(?<![A-Za-zÀ-ÿ0-9])R(?![A-Za-zÀ-ÿ0-9])", "A");

        // 2. High-confidence non-English pixel-art confusions (Y->V, D->O, A->R in mid-vowel positions), preserving case
        s = ReplacePreservingCase(s, @"\bvou\b", "you");
        s = ReplacePreservingCase(s, @"\bvour\b", "your");
        s = Regex.Replace(s, @"\b(how|what|why|where|when)\s+oo\b", "$1 do", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\boo\s+(you|not|it)\b", "do $1", RegexOptions.IgnoreCase);
        s = ReplacePreservingCase(s, @"\bplerse\b", "please");
        s = ReplacePreservingCase(s, @"\bcrertion\b", "creation");
        s = ReplacePreservingCase(s, @"\boisgust\b", "disgust");
        s = ReplacePreservingCase(s, @"\bherrt\b", "heart");
        s = Regex.Replace(s, @"\bplry(ing|ed|s|er)?\b", m => (char.IsUpper(m.Value[0]) ? "Play" : "play") + m.Groups[1].Value, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bstry(ing|ed|s)?\b", m => (char.IsUpper(m.Value[0]) ? "Stay" : "stay") + m.Groups[1].Value, RegexOptions.IgnoreCase);

        // 3. General phonotactic word-level repair for impossible English R-clusters (pixel-art A -> R)
        s = Regex.Replace(s, @"\b[A-Za-z]{2,}\b", match => RepairPixelRWord(match.Value));

        return s;
    }

    private static string ReplacePreservingCase(string input, string pattern, string replacementLower)
    {
        return Regex.Replace(input, pattern, m =>
        {
            if (m.Value.Length > 0 && char.IsUpper(m.Value[0]))
                return char.ToUpperInvariant(replacementLower[0]) + replacementLower[1..];
            return replacementLower;
        }, RegexOptions.IgnoreCase);
    }

    private static string RepairPixelRWord(string word)
    {
        if (ExemptShortTokens.Contains(word))
            return word;

        string w = word;

        // Rule A: No English word starts with 'r' + consonant (except 'rh'+vowel like rhythm/rhino)
        // Fixes: rbout->about, rmbition->ambition, rnd->and, rll->all, rre->are, rfter->after,
        //        rgain->again, rlways->always, rny->any, rsk->ask, rt->at, rs->as, rm->am, rn->an, rhbition->ambition
        if (Regex.IsMatch(w, @"^[rR][bcdfgjklmnpqrstvwxz]") ||
            Regex.IsMatch(w, @"^[rR][hH][bcdfgjklmnpqrstvwxz]"))
        {
            char repl = char.IsUpper(w[0]) ? 'A' : 'a';
            w = repl + w[1..];
        }

        // Rule B1: Valid onset blend + 'r' (br/cr/dr/fr/gr/pr/tr/str) + pixel-art 'r' (was 'a') + C + vowel
        // Fixes: brrvery->bravery, trrvel->travel, drrgon->dragon, frrme->frame, grrve->grave
        w = Regex.Replace(w, @"^(str|[bcdfgpstBCDFGPST][rR])([rR])(?=[bcdfghjklmnpqstvwxzBCDFGHJKLMNPQSTVWXZ][aeiouyAEIOUY])", m =>
        {
            char repl = char.IsUpper(m.Groups[2].Value[0]) ? 'A' : 'a';
            return m.Groups[1].Value + repl;
        });

        // Rule B2: No English word starts with Consonant(s) + 'r' + Consonant (excluding 'y' so cry/try/dry/fry/wry stay intact!)
        // Fixes: thrt->that, whrt->what, hrve->have, nrme->name, mrke->make, trke->take, srve->save,
        //        grme->game, wrnt->want, brck->back, trlk->talk, wrlk->walk, crn->can, wrs->was, hrs->has,
        //        drrk->dark, strrt->start, frce->face, hrte->hate, lrte->late, grte->gate, crse->case
        w = Regex.Replace(w, @"^([bcdfghjklmnpqstvwxzBCDFGHJKLMNPQSTVWXZ]+)([rR])(?=[bcdfghjklmnpqrstvwxzBCDFGHJKLMNPQRSTVWXZ])", m =>
        {
            char repl = char.IsUpper(m.Groups[2].Value[0]) ? 'A' : 'a';
            return m.Groups[1].Value + repl;
        });

        // Rule C: Words of length >= 4 ending in Consonant + "err" -> Consonant + "ear"
        // Fixes: ferr->fear, herr->hear, nerr->near, derr->dear, clerr->clear, yerr->year, terr->tear, werr->wear, berr->bear, rpperr->appear
        if (w.Length >= 4 && Regex.IsMatch(w, @"[bcdfghjklmnpqrstvwxz]err$", RegexOptions.IgnoreCase))
        {
            bool upper = char.IsUpper(w[^2]);
            w = w[..^2] + (upper ? "AR" : "ar");
        }

        // Rule D: Internal "r" between consonants in words that still have ZERO vowels (e.g. "strnd", "prst", "lrst", "frst", "hrnd")
        if (w.Length >= 3 && !Regex.IsMatch(w, @"[aeiouyAEIOUY]") && w.IndexOfAny(new[] { 'r', 'R' }) >= 0)
        {
            int idx = w.IndexOfAny(new[] { 'r', 'R' });
            char repl = char.IsUpper(w[idx]) ? 'A' : 'a';
            w = w[..idx] + repl + w[(idx + 1)..];
        }

        return w;
    }

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 4;
        byte[] pixels = new byte[rowBytes * height];

        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Ignore);
    }
}
