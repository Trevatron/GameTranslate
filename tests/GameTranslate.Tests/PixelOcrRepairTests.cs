using GameTranslate.Services;
using Xunit;

namespace GameTranslate.Tests;

public sealed class PixelOcrRepairTests
{
    [Theory]
    [InlineData("Give it r name.", "Give it a name.")]
    [InlineData("R gift for you.", "A gift for you.")]
    [InlineData("Find r key in r dark room.", "Find a key in a dark room.")]
    public void StandaloneR_IsRepairedToArticleA(string input, string expected)
    {
        Assert.Equal(expected, WindowsOcrService.CleanCommonPixelOcrGlitches(input));
    }

    [Theory]
    [InlineData("rbout", "about")]
    [InlineData("Rmbition", "Ambition")]
    [InlineData("rnd", "and")]
    [InlineData("rll", "all")]
    [InlineData("rre", "are")]
    [InlineData("rfter", "after")]
    [InlineData("rgain", "again")]
    [InlineData("rlways", "always")]
    [InlineData("rny", "any")]
    [InlineData("rsk", "ask")]
    [InlineData("rt", "at")]
    [InlineData("Rhbition", "Ahbition")]
    public void InitialRPlusConsonant_IsRepairedToA(string input, string expected)
    {
        Assert.Equal(expected, WindowsOcrService.CleanCommonPixelOcrGlitches(input));
    }

    [Theory]
    [InlineData("Whrt is thrt?", "What is that?")]
    [InlineData("I hrve r nrme.", "I have a name.")]
    [InlineData("Mrke sure to srve the grme.", "Make sure to save the game.")]
    [InlineData("We crn trlk rnd wrlk brck.", "We can talk and walk back.")]
    [InlineData("Drrk Strrt Brrvery Trrvel Drrgon", "Dark Start Bravery Travel Dragon")]
    [InlineData("Plerse open your crertion.", "Please open your creation.")]
    [InlineData("How oo vou feel rbout vour herrt?", "How do you feel about your heart?")]
    public void MedialPixelRAndCommonGlitches_AreRepairedAccurately(string input, string expected)
    {
        Assert.Equal(expected, WindowsOcrService.CleanCommonPixelOcrGlitches(input));
    }

    [Theory]
    [InlineData("Ferr", "Fear")]
    [InlineData("herr", "hear")]
    [InlineData("nerr", "near")]
    [InlineData("Clerr", "Clear")]
    [InlineData("derr", "dear")]
    [InlineData("terr", "tear")]
    public void TrailingErr_IsRepairedToEar(string input, string expected)
    {
        Assert.Equal(expected, WindowsOcrService.CleanCommonPixelOcrGlitches(input));
    }

    [Theory]
    [InlineData("Three strong sprites scream across the street.")]
    [InlineData("Rhythm and rhyme with a rhino.")]
    [InlineData("Dr. Smith cried dry tears.")]
    [InlineData("Try to press the world heart with a sword.")]
    [InlineData("Mr. Fox pressed Ctrl in this RPG.")]
    [InlineData("Do not err in front of the shrine.")]
    [InlineData("Fry and pry the spring scroll.")]
    public void ValidEnglishWords_AreNeverAltered(string validSentence)
    {
        Assert.Equal(validSentence, WindowsOcrService.CleanCommonPixelOcrGlitches(validSentence));
    }

    [Fact]
    public void FullPixelArtSentence_FixesAllGlitchesAtOnce()
    {
        const string input = "Give it r nrme. Whrt is thrt? Rll of us crn trlk rnd srve the drrk world. I ferr nothing. Rmbition Brrvery Clerr";
        const string expected = "Give it a name. What is that? All of us can talk and save the dark world. I fear nothing. Ambition Bravery Clear";

        Assert.Equal(expected, WindowsOcrService.CleanCommonPixelOcrGlitches(input));
    }
}
