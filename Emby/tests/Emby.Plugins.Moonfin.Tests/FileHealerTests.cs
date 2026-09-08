using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// FileHealer walks the settings directory and puts each file through a ladder: leave it alone if
/// it parses, else recover a .bak, else salvage it, else promote a .tmp, else quarantine it. It is
/// hand-copied between the two plugins and carries a keep-in-sync header.
/// </summary>
public class FileHealerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"moonfin-emby-healer-{Guid.NewGuid():N}");

    private string Quarantine => Path.Combine(_root, "quarantine");

    /// <summary>One file per rung of the ladder, plus a name the healer must not touch.</summary>
    private sealed class Layout
    {
        public HealSummary Summary { get; init; } = new();
        public string Healthy { get; init; } = string.Empty;
        public DateTime HealthyWrittenUtc { get; init; }
        public string Truncated { get; init; } = string.Empty;
        public string Empty { get; init; } = string.Empty;
        public string Nul { get; init; } = string.Empty;
        public string BakRecovery { get; init; } = string.Empty;
        public string TmpPromote { get; init; } = string.Empty;
        public string StaleTmp { get; init; } = string.Empty;
        public string NotAUser { get; init; } = string.Empty;
    }

    private async Task<Layout> RunHealAsync()
    {
        Directory.CreateDirectory(_root);
        var full = SettingsFixture.Full;

        string NewUserFile(string contents)
        {
            var path = Path.Combine(_root, $"{Guid.NewGuid()}.json");
            File.WriteAllText(path, contents);
            return path;
        }

        var healthy = NewUserFile(full);
        var healthyWrite = File.GetLastWriteTimeUtc(healthy);

        var truncated = NewUserFile(full.Substring(0, SettingsFixture.GlobalEnd + 5));
        var empty = NewUserFile(string.Empty);
        var nul = NewUserFile(new string((char)0, 2048));

        var bakRecovery = NewUserFile("{\"schemaVersion\":2,\"glo");
        File.WriteAllText(bakRecovery + ".bak", full);

        var tmpPromote = Path.Combine(_root, $"{Guid.NewGuid()}.json");
        File.WriteAllText(tmpPromote + ".tmp", full);

        var staleTmp = NewUserFile(full);
        File.WriteAllText(staleTmp + ".tmp", "half-written");

        var notAUser = Path.Combine(_root, "not-a-user.json");
        File.WriteAllText(notAUser, "{broken");

        var gate = new SemaphoreSlim(1, 1);
        var summary = await FileHealer.HealDirectoryAsync(
            _root,
            Quarantine,
            gate,
            SettingsFixture.ValidEnvelope,
            SettingsFixture.Salvage,
            CancellationToken.None);

        return new Layout
        {
            Summary = summary,
            Healthy = healthy,
            HealthyWrittenUtc = healthyWrite,
            Truncated = truncated,
            Empty = empty,
            Nul = nul,
            BakRecovery = bakRecovery,
            TmpPromote = tmpPromote,
            StaleTmp = staleTmp,
            NotAUser = notAUser,
        };
    }

    [Fact]
    public async Task HealDirectory_CountsEveryRungOfTheLadder()
    {
        var layout = await RunHealAsync();
        var summary = layout.Summary;

        Assert.Equal(7, summary.Scanned);
        Assert.Equal(2, summary.Healthy);
        Assert.Equal(1, summary.Salvaged);
        Assert.Equal(1, summary.RecoveredFromBackup);
        Assert.Equal(1, summary.TmpPromoted);
        Assert.Equal(2, summary.Quarantined);
        Assert.Equal(0, summary.Errors);
    }

    [Fact]
    public async Task HealDirectory_LeavesAHealthyFileCompletelyAlone()
    {
        var layout = await RunHealAsync();

        Assert.Equal(SettingsFixture.Full, File.ReadAllText(layout.Healthy));
        Assert.Equal(layout.HealthyWrittenUtc, File.GetLastWriteTimeUtc(layout.Healthy));
    }

    [Fact]
    public async Task HealDirectory_SalvagesATruncatedFileWithoutLosingGlobal()
    {
        var layout = await RunHealAsync();

        var salvaged = SettingsFixture.Parse(File.ReadAllText(layout.Truncated));

        Assert.NotNull(salvaged?.Global);
        Assert.Equal("key,\"quoted\",end", salvaged!.Global!.SeerrApiKey);
    }

    [Fact]
    public async Task HealDirectory_RecoversFromABackupBeforeGivingUp()
    {
        var layout = await RunHealAsync();

        Assert.NotNull(SettingsFixture.Parse(File.ReadAllText(layout.BakRecovery))?.Tv);
    }

    [Fact]
    public async Task HealDirectory_PromotesATmpWhenTheMainFileIsMissing()
    {
        var layout = await RunHealAsync();

        Assert.NotNull(SettingsFixture.Parse(File.ReadAllText(layout.TmpPromote))?.Tv);
    }

    [Fact]
    public async Task HealDirectory_ClearsAStaleTmpAndKeepsTheHealthyMainBesideIt()
    {
        var layout = await RunHealAsync();

        Assert.False(File.Exists(layout.StaleTmp + ".tmp"));
        Assert.Equal(SettingsFixture.Full, File.ReadAllText(layout.StaleTmp));
    }

    [Fact]
    public async Task HealDirectory_MovesOutWhatItCannotRepair()
    {
        var layout = await RunHealAsync();

        Assert.False(File.Exists(layout.Empty));
        Assert.False(File.Exists(layout.Nul));

        var quarantined = Directory.Exists(Quarantine)
            ? Directory.GetFiles(Quarantine)
            : Array.Empty<string>();

        // empty, NUL, stale tmp, pre-salvage copy, pre-recovery copy.
        Assert.Equal(5, quarantined.Length);
    }

    [Fact]
    public async Task HealDirectory_IgnoresAFileThatIsNotNamedForAUser()
    {
        var layout = await RunHealAsync();

        Assert.True(File.Exists(layout.NotAUser));
        Assert.Equal("{broken", File.ReadAllText(layout.NotAUser));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
