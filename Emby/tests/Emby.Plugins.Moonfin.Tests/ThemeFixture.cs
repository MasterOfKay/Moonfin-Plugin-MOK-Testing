using System.Text.Json;
using System.Text.Json.Nodes;
using Emby.Plugins.Moonfin.Models;
using Emby.Plugins.Moonfin.Services;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// Builds a theme the validator accepts, so each test can break one rule and assert on that alone.
/// The key lists mirror the required sets in MoonfinThemeValidator.
/// </summary>
internal static class ThemeFixture
{
    private const string Colour = "#FF112233";

    private static readonly string[] ColorKeys =
    {
        "background", "onBackground", "surface", "onSurface", "surfaceVariant", "scrim",
        "accent", "onAccent", "buttonNormal", "buttonFocused", "buttonDisabled", "buttonActive",
        "onButtonNormal", "onButtonFocused", "onButtonDisabled", "inputBackground", "inputFocused",
        "inputBorder", "inputBorderFocused", "rangeTrack", "rangeProgress", "rangeThumb",
        "seekbarBuffered", "badgeBackground", "onBadge", "badgeUnplayed", "badgeWatched",
        "recordingActive", "recordingScheduled"
    };

    private static readonly string[] SemanticKeys =
    {
        "statusAvailable", "statusRequested", "statusPending", "statusDownloading",
        "mediaTypeBadgeMovie", "mediaTypeBadgeShow"
    };

    private static readonly string[] BookKeys =
    {
        "background", "accent", "mutedText", "primaryText", "sectionTitle", "divider",
        "placeholder", "shadow", "gradientTop", "gradientBottom", "inactiveChip"
    };

    private static JsonObject Colours(string[] keys)
    {
        var obj = new JsonObject();
        foreach (var key in keys)
        {
            obj[key] = Colour;
        }

        return obj;
    }

    private static JsonObject Border() => new() { ["color"] = Colour, ["width"] = 2 };

    /// <summary>A palette of <paramref name="count"/> valid colours, for the length limits.</summary>
    public static JsonArray Palette(int count)
    {
        var array = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            array.Add(Colour);
        }

        return array;
    }

    public static JsonObject Shadow(double spread = 0) => new()
    {
        ["color"] = Colour,
        ["blurRadius"] = 4,
        ["offsetX"] = 0,
        ["offsetY"] = 1,
        ["spreadRadius"] = spread,
    };

    public static JsonObject Valid() => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = "midnight-blue",
        ["displayName"] = "Midnight Blue",
        ["colors"] = Colours(ColorKeys),
        ["semantic"] = Colours(SemanticKeys),
        ["book"] = new JsonObject(Colours(BookKeys).ToDictionary(p => p.Key, p => p.Value?.DeepClone()))
        {
            ["placeholderPalette"] = new JsonArray(Colour),
        },
        ["borders"] = new JsonObject
        {
            ["cardBorder"] = Border(),
            ["chipBorder"] = Border(),
            ["focusBorder"] = Border(),
            ["cardRadius"] = 8,
            ["chipRadius"] = 4,
            ["chipBackground"] = Colour,
            ["focusGlow"] = new JsonArray(Shadow(2)),
        },
    };

    public static MoonfinThemeValidationResult Validate(JsonObject theme) =>
        new MoonfinThemeValidator().Validate(
            JsonSerializer.Deserialize<JsonElement>(theme.ToJsonString()));

    public static MoonfinThemeValidationResult ValidateWith(Action<JsonObject> mutate)
    {
        var theme = Valid();
        mutate(theme);
        return Validate(theme);
    }
}
