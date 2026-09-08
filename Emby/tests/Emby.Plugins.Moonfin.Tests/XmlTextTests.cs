using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Plugin configuration is serialized to XML, and a writer that emits a character the XML 1.0 Char
/// production can't carry produces a file a strict reader then refuses, losing the configuration.
/// XmlText is the guard on every piece of free text that reaches the config, and it is hand-copied
/// between the two plugins.
///
/// Cases are built from code points rather than character literals, so each one names the
/// character it tests and the file stays free of invisible characters.
/// </summary>
public class XmlTextTests
{
    private const int HighSurrogate = 0xD83D;
    private const int LowSurrogate = 0xDE80;

    private static string Rocket => char.ConvertFromUtf32(0x1F680);

    private static string Around(int codePoint) => "a" + (char)codePoint + "b";

    [Fact]
    public void Sanitize_TreatsNullAndEmptyAsEmpty()
    {
        Assert.Equal(string.Empty, XmlText.Sanitize(null));
        Assert.Equal(string.Empty, XmlText.Sanitize(string.Empty));
    }

    [Theory]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x0D)]
    public void Sanitize_KeepsTheThreeControlsXmlAllows(int codePoint)
    {
        var text = Around(codePoint);

        Assert.Equal(text, XmlText.Sanitize(text));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x08)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x1F)]
    public void Sanitize_DropsTheC0ControlsXmlCantCarry(int codePoint)
    {
        Assert.Equal("ab", XmlText.Sanitize(Around(codePoint)));
    }

    [Theory]
    [InlineData(0x7F)]
    [InlineData(0x80)]
    [InlineData(0x9F)]
    public void Sanitize_KeepsDelAndTheC1ControlsXmlAllows(int codePoint)
    {
        // Legal in XML 1.0 and deliberately kept, per the IsLegal doc comment.
        var text = Around(codePoint);

        Assert.Equal(text, XmlText.Sanitize(text));
    }

    [Fact]
    public void Sanitize_KeepsAPairedAstralCharacter()
    {
        var text = "a" + Rocket + "b";

        Assert.Equal(text, XmlText.Sanitize(text));
    }

    [Fact]
    public void Sanitize_DropsAHighSurrogateWithNothingAfterIt()
    {
        Assert.Equal("a", XmlText.Sanitize("a" + (char)HighSurrogate));
        Assert.Equal("ab", XmlText.Sanitize(Around(HighSurrogate)));
    }

    [Fact]
    public void Sanitize_DropsALowSurrogateWithNothingBeforeIt()
    {
        Assert.Equal("ab", XmlText.Sanitize(Around(LowSurrogate)));
    }

    [Fact]
    public void Sanitize_DropsThePairAtTheEndOfThePlaneButKeepsTheReplacementChar()
    {
        Assert.Equal("ab", XmlText.Sanitize(Around(0xFFFE)));
        Assert.Equal("ab", XmlText.Sanitize(Around(0xFFFF)));
        Assert.Equal(Around(0xFFFD), XmlText.Sanitize(Around(0xFFFD)));
    }

    [Fact]
    public void SanitizeOrNull_AnswersNullWhenNothingUsableSurvives()
    {
        Assert.Null(XmlText.SanitizeOrNull(null));
        Assert.Null(XmlText.SanitizeOrNull(string.Empty));
        Assert.Null(XmlText.SanitizeOrNull(((char)0x01).ToString()));
        Assert.Null(XmlText.SanitizeOrNull("   "));
    }

    [Fact]
    public void SanitizeOrNull_AnswersTheCleanedTextWhenSomethingSurvives()
    {
        Assert.Equal("ab", XmlText.SanitizeOrNull("ab"));
    }

    [Fact]
    public void Truncate_LeavesTextThatAlreadyFits()
    {
        Assert.Equal("abc", XmlText.Truncate("abc", 3));
        Assert.Equal("abc", XmlText.Truncate("abc", 10));
        Assert.Equal(string.Empty, XmlText.Truncate(string.Empty, 3));
    }

    [Fact]
    public void Truncate_CutsToTheCap()
    {
        Assert.Equal("abc", XmlText.Truncate("abcdef", 3));
    }

    [Fact]
    public void Truncate_NeverCutsThroughASurrogatePair()
    {
        // Cutting at 2 would leave the high surrogate on its own, which is the very thing
        // Sanitize exists to remove.
        var text = "a" + Rocket + "b";

        Assert.Equal("a", XmlText.Truncate(text, 2));
        Assert.Equal("a" + Rocket, XmlText.Truncate(text, 3));
    }

    [Fact]
    public void Truncate_LeavesNothingForSanitizeToStrip()
    {
        var cut = XmlText.Truncate("a" + Rocket + "b", 2);

        Assert.Equal(cut, XmlText.Sanitize(cut));
    }
}
