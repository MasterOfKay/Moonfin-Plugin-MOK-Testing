using Emby.Plugins.Moonfin.Api;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// An uploaded report is named on the server from the caller's own session, never from anything
/// in the request, so these cover what a client name is allowed to turn into.
/// </summary>
public class ClientLogServiceTests
{
    [Fact]
    public void Sanitize_KeepsAnOrdinaryName()
    {
        Assert.Equal("Moonfin", ClientLogService.Sanitize("Moonfin", "client"));
    }

    [Fact]
    public void Sanitize_KeepsTheDotsInAVersion()
    {
        Assert.Equal("2.5.1", ClientLogService.Sanitize("2.5.1", "0"));
    }

    [Theory]
    [InlineData("Moonfin for Android TV", "Moonfin-for-Android-TV")]
    [InlineData("Moonfin for tvOS", "Moonfin-for-tvOS")]
    [InlineData("  padded  name  ", "padded-name")]
    public void Sanitize_TurnsRunsOfSpacesIntoOneDash(string value, string expected)
    {
        // Every client name the app sends has spaces in it, and dropping them outright would
        // leave MoonfinforAndroidTV in the file name.
        Assert.Equal(expected, ClientLogService.Sanitize(value, "client"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b\\c")]
    [InlineData("name\r\nInjected")]
    public void Sanitize_DropsSeparatorsAndControlCharacters(string value)
    {
        var result = ClientLogService.Sanitize(value, "client");

        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
        Assert.DoesNotContain('\r', result);
        Assert.DoesNotContain('\n', result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("///")]
    public void Sanitize_FallsBackWhenNothingUsableIsLeft(string? value)
    {
        Assert.Equal("client", ClientLogService.Sanitize(value, "client"));
    }

    [Fact]
    public void Sanitize_CapsTheLength()
    {
        Assert.Equal(32, ClientLogService.Sanitize(new string('a', 100), "client").Length);
    }

    [Fact]
    public void BuildFileName_NamesTheReportAfterTheClientAndUtcTime()
    {
        var when = new DateTime(2026, 9, 13, 23, 20, 45, 142, DateTimeKind.Utc);

        Assert.Equal(
            "upload_Moonfin_2.5.1_20260913T232045142.txt",
            ClientLogService.BuildFileName("Moonfin", "2.5.1", when, 0));
    }

    [Fact]
    public void BuildFileName_AddsACounterOnlyAfterTheFirstAttempt()
    {
        var when = new DateTime(2026, 9, 13, 23, 20, 45, 142, DateTimeKind.Utc);

        Assert.Equal(
            "upload_Moonfin_2.5.1_20260913T232045142_1.txt",
            ClientLogService.BuildFileName("Moonfin", "2.5.1", when, 1));
    }

    [Fact]
    public void BuildFileName_EndsInTxtSoEmbyListsIt()
    {
        // Emby writes its own logs as .txt, and a report named to match lists beside them under
        // Dashboard > Logs.
        Assert.EndsWith(".txt", ClientLogService.BuildFileName("Moonfin", "2.5.1", DateTime.UtcNow, 0));
    }

    [Fact]
    public void BuildFileName_StaysInsideTheLogFolderEvenWhenTheClientLies()
    {
        var folder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "moonfin-client-log-test"));

        var name = ClientLogService.BuildFileName(
            ClientLogService.Sanitize("../../etc", "client"),
            ClientLogService.Sanitize("../1", "0"),
            DateTime.UtcNow,
            0);

        var combined = Path.GetFullPath(Path.Combine(folder, name));

        Assert.Equal(folder, Path.GetDirectoryName(combined));
    }
}
