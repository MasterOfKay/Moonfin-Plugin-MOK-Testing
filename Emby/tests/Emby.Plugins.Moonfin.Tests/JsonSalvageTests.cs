using System.Text.Json;
using Emby.Plugins.Moonfin.Services;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// JsonSalvage repairs a settings file truncated by a half-finished write. It is hand-copied
/// between the two plugins and carries a keep-in-sync header, so these are the assertions the
/// Jellyfin harness makes, restated as tests.
/// </summary>
public class JsonSalvageTests
{
    private static bool Valid(string text) => SettingsFixture.ValidEnvelope(text);

    [Fact]
    public void TrySalvage_HandlesEveryTruncationOfTheEnvelope()
    {
        // One pass per byte rather than one named case per offset, which would be a few thousand
        // tests saying the same thing.
        var full = SettingsFixture.Full;

        for (var i = 0; i < full.Length; i++)
        {
            var cut = full.Substring(0, i);
            bool ok;
            string healed;

            try
            {
                ok = JsonSalvage.TrySalvage(cut, Valid, out healed);
            }
            catch (Exception ex)
            {
                Assert.Fail($"offset {i}: threw {ex.GetType().Name}");
                return;
            }

            if (ok)
            {
                Assert.True(Valid(healed), $"offset {i}: healed text fails the floor");
            }

            if (i > SettingsFixture.GlobalEnd)
            {
                Assert.True(ok, $"offset {i}: salvage failed past global");

                var salvagedGlobal = JsonSerializer.Serialize(
                    SettingsFixture.Parse(healed)!.Global, SettingsFixture.JsonOptions);
                Assert.True(
                    salvagedGlobal == SettingsFixture.OriginalGlobal,
                    $"offset {i}: global not preserved");
            }
        }
    }

    [Fact]
    public void TrySalvage_LeavesAnUntruncatedDocumentIntact()
    {
        Assert.True(JsonSalvage.TrySalvage(SettingsFixture.Full, Valid, out var healed));
        Assert.Equal("tv{}[]\\,", SettingsFixture.Parse(healed)!.Tv?.SeerrApiKey);
    }

    [Fact]
    public void TrySalvage_RejectsInputWithNothingToRecover()
    {
        Assert.False(JsonSalvage.TrySalvage(string.Empty, Valid, out _));
        Assert.False(JsonSalvage.TrySalvage("   \n\t ", Valid, out _));
        Assert.False(JsonSalvage.TrySalvage(new string((char)0, 4096), Valid, out _));
        Assert.False(JsonSalvage.TrySalvage("[1,2,3]", Valid, out _));
    }

    [Fact]
    public void TrySalvage_TreatsANulTailAsTruncationAtThatPoint()
    {
        var withNulTail = SettingsFixture.Full.Substring(0, SettingsFixture.GlobalEnd + 5)
            + new string((char)0, 512);

        Assert.True(JsonSalvage.TrySalvage(withNulTail, Valid, out var healed));
        Assert.NotNull(SettingsFixture.Parse(healed)!.Global);
    }

    [Fact]
    public void TrySalvage_LooksPastAByteOrderMark()
    {
        var bom = ((char)0xFEFF).ToString();

        Assert.True(JsonSalvage.TrySalvage(bom + SettingsFixture.Full, Valid, out _));
        Assert.True(JsonSalvage.TrySalvage(
            bom + SettingsFixture.Full.Substring(0, SettingsFixture.GlobalEnd + 5), Valid, out _));
    }

    [Fact]
    public void TrySalvage_StripsGarbageTrailingAValidDocument()
    {
        Assert.True(JsonSalvage.TrySalvage(SettingsFixture.Full + "garbage{{{", Valid, out var healed));
        Assert.NotNull(SettingsFixture.Parse(healed)!.Tv);
    }

    [Fact]
    public void TrySalvage_RejectsACutInsideGlobal()
    {
        // What remains has no profile and no legacy fields, so the floor rejects it and the file
        // goes to quarantine rather than being written back half empty.
        var insideGlobal = SettingsFixture.Full.Substring(
            0, SettingsFixture.GlobalStart + "\"global\"".Length + 6);

        Assert.False(JsonSalvage.TrySalvage(insideGlobal, Valid, out _));
    }

    [Fact]
    public void TrySalvage_RejectsAMetadataOnlyEnvelope()
    {
        Assert.False(JsonSalvage.TrySalvage(
            "{\"schemaVersion\":2,\"syncEnabled\":true}", Valid, out _));
    }

    [Fact]
    public void TrySalvage_KeepsATruncatedV1EnvelopeThroughNeedsMigration()
    {
        var v1 = "{\"schemaVersion\":1,\"navbarEnabled\":true,\"mediaBarEnabled\":false,\"unfinished\":\"cut";

        Assert.True(JsonSalvage.TrySalvage(v1, Valid, out var healed));
        Assert.True(SettingsFixture.Parse(healed)!.NeedsMigration);
    }
}
