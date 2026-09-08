using System.Globalization;
using System.Text.Json;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// MDBList answers with fields that change type between responses, and TMDB hands back absolute
/// image URLs the client can't use directly. Both are cleaned up on the way in.
/// </summary>
public class MdbListHelperTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new TolerantStringConverter() },
    };

    private static string? ReadString(string json) =>
        JsonSerializer.Deserialize<string?>(json, Options);

    [Fact]
    public void TolerantStringConverter_ReadsAnOrdinaryString()
    {
        Assert.Equal("poster.jpg", ReadString("\"poster.jpg\""));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    public void TolerantStringConverter_TreatsNullAndBooleansAsAbsent(string json)
    {
        // A boolean where a URL belongs means "no image", not the text "false".
        Assert.Null(ReadString(json));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("42", "42")]
    [InlineData("-7", "-7")]
    public void TolerantStringConverter_TurnsAWholeNumberIntoItsDigits(string json, string expected)
    {
        Assert.Equal(expected, ReadString(json));
    }

    [Fact]
    public void TolerantStringConverter_TurnsAFractionalNumberIntoText()
    {
        // The production path formats with the current culture, so the expectation follows it
        // rather than hard-coding a decimal point.
        Assert.Equal(3.5d.ToString(CultureInfo.CurrentCulture), ReadString("3.5"));
    }

    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2,3]")]
    public void TolerantStringConverter_SkipsAWholeObjectOrArray(string json)
    {
        Assert.Null(ReadString(json));
    }

    [Fact]
    public void TolerantStringConverter_WritesBackAStringOrNull()
    {
        Assert.Equal("\"abc\"", JsonSerializer.Serialize<string?>("abc", Options));
        Assert.Equal("null", JsonSerializer.Serialize<string?>(null, Options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeTmdbImagePath_AnswersNullForNothingUsable(string? url)
    {
        Assert.Null(MdbListApiHelper.NormalizeTmdbImagePath(url));
    }

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/w200/abc123.jpg", "/abc123.jpg")]
    [InlineData("https://image.tmdb.org/t/p/original/abc123.jpg", "/abc123.jpg")]
    [InlineData("http://image.tmdb.org/t/p/w500/x/y.jpg", "/x/y.jpg")]
    public void NormalizeTmdbImagePath_DropsTheHostAndTheSizeSegment(string url, string expected)
    {
        // The size is chosen per request, so only the path below it is worth storing.
        Assert.Equal(expected, MdbListApiHelper.NormalizeTmdbImagePath(url));
    }

    [Fact]
    public void NormalizeTmdbImagePath_LeavesARelativePathAlone()
    {
        Assert.Equal("/abc123.jpg", MdbListApiHelper.NormalizeTmdbImagePath("/abc123.jpg"));
    }

    [Theory]
    [InlineData("https://example.com/poster.jpg")]
    [InlineData("https://image.tmdb.org/other/abc.jpg")]
    public void NormalizeTmdbImagePath_LeavesAUrlThatIsNotATmdbImageAlone(string url)
    {
        Assert.Equal(url, MdbListApiHelper.NormalizeTmdbImagePath(url));
    }

    [Fact]
    public void NormalizeTmdbImagePath_LeavesTheUrlAloneWhenNothingFollowsTheSize()
    {
        const string url = "https://image.tmdb.org/t/p/w200";

        Assert.Equal(url, MdbListApiHelper.NormalizeTmdbImagePath(url));
    }
}
