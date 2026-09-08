using System.Text.Json;
using System.Text.Json.Serialization;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// The settings envelope the salvage and heal tests both work from, and the two predicates the
/// production code hands to JsonSalvage and FileHealer.
/// </summary>
internal static class SettingsFixture
{
    /// <summary>Mirrors MoonfinSettingsService._jsonOptions.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MoonfinUserSettings? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<MoonfinUserSettings>(text, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Mirrors the validation floor in MoonfinSettingsService.HealDataFilesAsync.</summary>
    public static bool ValidEnvelope(string text)
    {
        var env = Parse(text);
        if (env == null || env.SchemaVersion is < 1 or > 2)
        {
            return false;
        }

        return env.Global != null || env.Desktop != null || env.Mobile != null || env.Tv != null ||
            env.NeedsMigration;
    }

    /// <summary>
    /// A full v2 envelope whose strings stress the scanner. Braces and brackets stay out of
    /// global's strings so <see cref="GlobalEnd"/> can find global's real closing brace by
    /// scanning. The nasty strings on either side still stress the scanner across the sweep.
    /// </summary>
    public static string Full { get; } = JsonSerializer.Serialize(
        new MoonfinUserSettings
        {
            SchemaVersion = 2,
            LastUpdated = 1721000000000,
            LastUpdatedBy = "client-with-\"quotes\", {braces} and \\ backslashes",
            SyncEnabled = true,
            Global = new MoonfinSettingsProfile { SeerrApiKey = "key,\"quoted\",end" },
            Desktop = new MoonfinSettingsProfile { SeerrBlockNsfw = true },
            Mobile = new MoonfinSettingsProfile { SeerrEnabled = false },
            Tv = new MoonfinSettingsProfile { SeerrApiKey = "tv{}[]\\," },
        },
        JsonOptions);

    /// <summary>Offset of the "global" property name in <see cref="Full"/>.</summary>
    public static int GlobalStart { get; } = Full.IndexOf("\"global\"", StringComparison.Ordinal);

    /// <summary>Offset just past the closing brace of global's object.</summary>
    public static int GlobalEnd { get; } = Full.IndexOf('}', GlobalStart) + 1;

    /// <summary>Global as the untruncated document serializes it, for comparing a salvaged copy.</summary>
    public static string OriginalGlobal { get; } =
        JsonSerializer.Serialize(Parse(Full)!.Global, JsonOptions);

    public static (bool Ok, string Healed) Salvage(string raw) =>
        (JsonSalvage.TrySalvage(raw, ValidEnvelope, out var healed), healed);
}
