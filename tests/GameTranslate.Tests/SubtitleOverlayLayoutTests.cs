using System.Threading;
using System.Windows;
using GameTranslate.Controls;
using Xunit;

namespace GameTranslate.Tests;

public sealed class SubtitleOverlayLayoutTests
{
    [Fact]
    public void OutlinedTextBlock_LongMultiLineDialogue_WrapsCleanlyWithinMaxWidth()
    {
        Exception? threadEx = null;
        Size measured = default;

        var thread = new Thread(() =>
        {
            try
            {
                var ctrl = new OutlinedTextBlock
                {
                    FontSize = 32,
                    StrokeThickness = 4.5,
                    TextAlignment = TextAlignment.Center
                };

                const string longText =
                    "É uma mesa de computador.\nHá muitas caixas embaixo dela cheias de livros antigos e anotações esquecidas há anos.";

                measured = ctrl.MeasureTextForFontSize(longText, maxAvailableWidth: 800, fontSize: 32);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
        Assert.True(measured.Width <= 800, $"Expected wrapped width <= 800, got {measured.Width}");
        Assert.True(measured.Height > 80, $"Expected multi-line height > 80, got {measured.Height}");
    }

    [Fact]
    public void OutlinedTextBlock_AutoFitShrinksFontSizeSoHugeParagraphNeverClips()
    {
        Exception? threadEx = null;
        double fittedFont = 0;
        Size finalSize = default;

        var thread = new Thread(() =>
        {
            try
            {
                var ctrl = new OutlinedTextBlock
                {
                    FontSize = 36,
                    StrokeThickness = 4.5,
                    TextAlignment = TextAlignment.Center
                };

                const string hugeParagraph =
                    "É uma mesa de computador muito antiga.\n" +
                    "Há muitas caixas empilhadas embaixo dela cheias de livros antigos, cadernos rasgados e mapas.\n" +
                    "Você percebe uma pequena chave dourada escondida entre as páginas amareladas.\n" +
                    "Talvez ela abra aquela porta trancada no corredor do segundo andar.";

                const double maxW = 700;
                const double maxH = 180;

                fittedFont = 36;
                while (fittedFont > 14)
                {
                    finalSize = ctrl.MeasureTextForFontSize(hugeParagraph, maxW, fittedFont);
                    if (finalSize.Height <= maxH && finalSize.Width <= maxW)
                        break;
                    fittedFont -= 2;
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
        Assert.True(fittedFont < 36, "Expected auto-fit to reduce font size for huge 4-line paragraph");
        Assert.True(finalSize.Width <= 700, $"Expected final width <= 700, got {finalSize.Width}");
        Assert.True(finalSize.Height <= 180, $"Expected final height <= 180 (zero clipping), got {finalSize.Height}");
    }
}
