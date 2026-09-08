using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The Seerr URL is pasted in by an admin and then concatenated with API paths, so it has to come
/// out of the config as an absolute http address or not at all. The migration alongside it carries
/// settings written before the Jellyseerr rename.
/// </summary>
public class PluginConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void NormalizeSeerrUrl_AnswersNullForNothingUsable(string? raw)
    {
        Assert.Null(PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Theory]
    [InlineData("\"http://seerr:5055\"")]
    [InlineData("'http://seerr:5055'")]
    [InlineData("  http://seerr:5055  ")]
    public void NormalizeSeerrUrl_StripsWhatPastingLeavesBehind(string raw)
    {
        Assert.Equal("http://seerr:5055", PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Fact]
    public void NormalizeSeerrUrl_AssumesHttpWhenNoSchemeIsGiven()
    {
        // Without this, Uri would read "seerr" as the scheme in "seerr:5055".
        Assert.Equal("http://seerr:5055", PluginConfiguration.NormalizeSeerrUrl("seerr:5055"));
        Assert.Equal("http://192.168.1.10:5055", PluginConfiguration.NormalizeSeerrUrl("192.168.1.10:5055"));
    }

    [Theory]
    [InlineData("http://seerr:5055/", "http://seerr:5055")]
    [InlineData("http://seerr:5055///", "http://seerr:5055")]
    [InlineData("https://seerr.example.com/", "https://seerr.example.com")]
    public void NormalizeSeerrUrl_TrimsTrailingSlashesSoPathsConcatenateCleanly(string raw, string expected)
    {
        Assert.Equal(expected, PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Theory]
    [InlineData("ftp://seerr:5055")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    public void NormalizeSeerrUrl_RejectsASchemeThatIsNotHttp(string raw)
    {
        Assert.Null(PluginConfiguration.NormalizeSeerrUrl(raw));
    }

    [Fact]
    public void NormalizeSeerrUrl_KeepsHttpsAndAPath()
    {
        Assert.Equal("https://example.com/seerr", PluginConfiguration.NormalizeSeerrUrl("https://example.com/seerr"));
    }

    [Fact]
    public void MigrateLegacyKeys_CopiesEveryLegacyValueAcross()
    {
        var config = new PluginConfiguration
        {
            JellyseerrUrl = "http://old:5055",
            JellyseerrEnabled = true,
            JellyseerrDisplayName = "Requests",
        };

        Assert.True(config.MigrateLegacyKeys());

        Assert.Equal("http://old:5055", config.SeerrUrl);
        Assert.True(config.SeerrEnabled);
        Assert.Equal("Requests", config.SeerrDisplayName);
    }

    [Fact]
    public void MigrateLegacyKeys_ClearsTheLegacyFieldsSoItOnlyRunsOnce()
    {
        var config = new PluginConfiguration { JellyseerrUrl = "http://old:5055" };

        Assert.True(config.MigrateLegacyKeys());
        Assert.Null(config.JellyseerrUrl);
        Assert.False(config.JellyseerrEnabled);
        Assert.Null(config.JellyseerrDisplayName);

        Assert.False(config.MigrateLegacyKeys());
    }

    [Fact]
    public void MigrateLegacyKeys_LeavesACurrentValueAlone()
    {
        var config = new PluginConfiguration
        {
            SeerrUrl = "http://new:5055",
            SeerrDisplayName = "Seerr",
            JellyseerrUrl = "http://old:5055",
            JellyseerrDisplayName = "Old",
        };

        config.MigrateLegacyKeys();

        Assert.Equal("http://new:5055", config.SeerrUrl);
        Assert.Equal("Seerr", config.SeerrDisplayName);
    }

    [Fact]
    public void MigrateLegacyKeys_NeverTurnsSeerrOffAgain()
    {
        // The enabled flag only ever migrates upward, so a legacy false can't disable a live setup.
        var config = new PluginConfiguration { SeerrEnabled = true, JellyseerrEnabled = false };

        config.MigrateLegacyKeys();

        Assert.True(config.SeerrEnabled);
    }

    [Fact]
    public void MigrateLegacyKeys_ReportsNoChangeOnAConfigWithNoLegacyValues()
    {
        Assert.False(new PluginConfiguration { SeerrUrl = "http://new:5055" }.MigrateLegacyKeys());
    }
}
