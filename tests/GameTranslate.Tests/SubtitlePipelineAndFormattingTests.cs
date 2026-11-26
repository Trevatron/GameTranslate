using GameTranslate.Services;
using Xunit;

namespace GameTranslate.Tests;

public sealed class SubtitlePipelineAndFormattingTests
{
    [Fact]
    public void Similarity_ExactTextIgnoringPunctuationAndCase_ReturnsOne()
    {
        double sim = SubtitlePipeline.ComputeNormalizedSimilarity(
            "Don't open that door!",
            "dont open that door");

        Assert.Equal(1.0, sim, 4);
    }

    [Fact]
    public void Similarity_SingleCharOcrJitter_ExceedsAntiFlickerThreshold()
    {
        double sim = SubtitlePipeline.ComputeNormalizedSimilarity(
            "We need to find the key before sunrise.",
            "We need to find the key before sunr1se.");

        Assert.True(sim >= 0.88, $"Expected >= 0.88 for 1-char jitter, got {sim:F3}");
    }

    [Fact]
    public void Similarity_CompletelyDifferentSentences_IsLow()
    {
        double sim = SubtitlePipeline.ComputeNormalizedSimilarity(
            "How do you feel about your creation?",
            "Don't open that door!");

        Assert.True(sim < 0.45, $"Expected < 0.45 for unrelated sentences, got {sim:F3}");
    }

    [Fact]
    public void Similarity_EmptyStringEdgeCases()
    {
        Assert.Equal(1.0, SubtitlePipeline.ComputeNormalizedSimilarity("", ""));
        Assert.Equal(0.0, SubtitlePipeline.ComputeNormalizedSimilarity("Hello", ""));
        Assert.Equal(0.0, SubtitlePipeline.ComputeNormalizedSimilarity("", "Hello"));
    }

    [Fact]
    public void FormatSubtitleOutput_CompactsThreeOrMoreMenuItemsAndCapitalizes()
    {
        const string input = "por favor, dê um presente.\nbondade\nmente\nambição\nbravura\nvoz";
        string formatted = TranslationService.FormatSubtitleOutput(input, compactMenuLines: true);

        const string expected = "Por favor, dê um presente.\nBondade  •  Mente  •  Ambição  •  Bravura  •  Voz";
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void FormatSubtitleOutput_WhenCompactDisabled_KeepsIndividualLines()
    {
        const string input = "amor\nesperança\nnojo\nmedo";
        string formatted = TranslationService.FormatSubtitleOutput(input, compactMenuLines: false);

        Assert.Equal("Amor\nEsperança\nNojo\nMedo", formatted);
    }

    [Fact]
    public void FormatSubtitleOutput_PreservesRegularMultiLineDialogue()
    {
        const string input = "como você se sente em relação à sua criação?\n(ela não ouvirá.)";
        string formatted = TranslationService.FormatSubtitleOutput(input, compactMenuLines: true);

        Assert.Equal("Como você se sente em relação à sua criação?\n(Ela não ouvirá.)", formatted);
    }

    [Fact]
    public void FormatSubtitleOutput_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TranslationService.FormatSubtitleOutput("   \r\n  ", true));
    }
}
