using System;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>
/// Handles persistence, validation, and formatting of LootSense user-facing preferences.
/// </summary>
internal sealed class LootSensePreferences
{
    private const string HighlightModePrefKey = "PMLootSense.HighlightMode";
    private const string SizePrefKey = "PMLootSense.SizePercent";
    private const string OpacityPrefKey = "PMLootSense.OpacityPercent";
    private const string ColorPrefKey = "PMLootSense.Color";
    private const string RangePrefKey = "PMLootSense.RangeBonus";

    private const float DefaultSizePercent = 15f;
    private const float DefaultOpacityPercent = 35f;
    private const string DefaultColorHex = "DAA520";
    private const float DefaultRangeBonusMeters = 4f;
    private const float RangeBonusMin = -10f;
    private const float RangeBonusMax = 30f;

    private const float IconBaseSize = 0.9f;

    /// <summary>
    /// Loads previously stored preferences from PlayerPrefs or falls back to defaults.
    /// </summary>
    public LootSensePreferences()
    {
        LoadHighlightPreference();
        LoadVisualPreferences();
    }

    public HighlightMode HighlightMode { get; private set; } = HighlightMode.Icon;
    public float SizePercent { get; private set; } = DefaultSizePercent;
    public float OpacityPercent { get; private set; } = DefaultOpacityPercent;
    public Color UserColor { get; private set; } = ParseDefaultColor();
    public float RangeBonusMeters { get; private set; } = DefaultRangeBonusMeters;

    public float SizeScale => Mathf.Clamp(SizePercent, 0f, 200f) / 100f;
    public float IconScale => IconBaseSize * Mathf.Max(SizeScale, 0f);
    public float Alpha => Mathf.Clamp01(OpacityPercent / 100f);
    public string ColorHex => ColorUtility.ToHtmlStringRGB(UserColor);

    /// <summary>
    /// Highlight mode is fixed to icon rendering; method retained for compatibility.
    /// </summary>
    public bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
    {
        mode = HighlightMode.Icon;
        message = "Highlight mode is locked to icon rendering.";
        return false;
    }

    /// <summary>
    /// Updates overlay opacity when a valid percent is provided.
    /// </summary>
    public bool TrySetOpacity(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 100f, out float value, out message))
            return false;

        OpacityPercent = value;
        SaveFloat(OpacityPrefKey, OpacityPercent);
        message = $"Opacity set to {value:F0}%";
        return true;
    }

    /// <summary>
    /// Sets icon/box scale based on a percent input.
    /// </summary>
    public bool TrySetSize(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 200f, out float value, out message))
            return false;

        SizePercent = value;
        SaveFloat(SizePrefKey, SizePercent);
        message = $"Size set to {value:F0}%";
        return true;
    }

    /// <summary>
    /// Accepts HTML-style hex colors and persists the parsed color.
    /// </summary>
    public bool TrySetColor(string token, out string message)
    {
        if (!TryParseColor(token, out var color))
        {
            message = "Invalid color. Use hex formats like FF0000 or #FF0000.";
            return false;
        }

        UserColor = color;
        SaveColorPreference();
        message = $"Color set to #{ColorHex}";
        return true;
    }

    /// <summary>
    /// Adds a delta to the range bonus while keeping it inside defined bounds.
    /// </summary>
    public bool TryAdjustRange(string token, out string message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            message = "Missing numeric value.";
            return false;
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float delta))
        {
            message = $"Could not parse '{token}' as a number.";
            return false;
        }

        RangeBonusMeters = Mathf.Clamp(RangeBonusMeters + delta, RangeBonusMin, RangeBonusMax);
        SaveFloat(RangePrefKey, RangeBonusMeters);
        message = $"Range bonus now {FormatMeters(RangeBonusMeters)}";
        return true;
    }

    /// <summary>
    /// Builds a compact status string showing user-selected values and derived ranges.
    /// </summary>
    public string BuildStatusSummary(float currentRadiusMeters)
    {
        return string.Format(CultureInfo.InvariantCulture,
            "mode={0} size={1:F0}% opacity={2:F0}% color=#{3} range={4} currentRange={5:F1}m",
            HighlightMode.ToString().ToLowerInvariant(),
            SizePercent,
            OpacityPercent,
            ColorHex,
            FormatMeters(RangeBonusMeters),
            currentRadiusMeters);
    }

    /// <summary>
    /// Produces a multi-line dump of stored preferences plus rank previews for debugging.
    /// </summary>
    public string BuildConfigDump(Func<int, float> rankPreviewFactory, float currentRadius)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LootSense configuration snapshot:");
        sb.AppendLine($"  mode={HighlightMode.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  sizePercent={SizePercent:F1}");
        sb.AppendLine($"  opacityPercent={OpacityPercent:F1}");
        sb.AppendLine($"  colorHex=#{ColorHex}");
        sb.AppendLine($"  rangeBonusMeters={RangeBonusMeters:F2}");
        sb.AppendLine($"  currentRadiusMeters={currentRadius:F2}");

        for (int rank = 1; rank <= 5; rank++)
            sb.AppendLine($"  rank{rank}RadiusMeters={rankPreviewFactory(rank):F2}");

        return sb.ToString();
    }

    /// <summary>
    /// Attempts to parse a floating-point percent value within the provided bounds.
    /// </summary>
    private static bool TryParsePercent(string token, float min, float max, out float value, out string message)
    {
        value = 0f;
        message = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            message = "Missing numeric value.";
            return false;
        }

        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
        {
            message = $"Could not parse '{token}' as a number.";
            return false;
        }

        value = Mathf.Clamp(parsed, min, max);
        return true;
    }

    /// <summary>
    /// Parses HTML hex colors, adding a leading # if the caller omitted it.
    /// </summary>
    private static bool TryParseColor(string token, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var trimmed = token.Trim();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            trimmed = "#" + trimmed;

        if (!ColorUtility.TryParseHtmlString(trimmed, out color))
            return false;

        color.a = 1f;
        return true;
    }

    /// <summary>
    /// Reads the persisted highlight mode, defaulting to icon when invalid.
    /// </summary>
    private void LoadHighlightPreference()
    {
        try
        {
            if (!PlayerPrefs.HasKey(HighlightModePrefKey))
            {
                SaveHighlightPreference(HighlightMode.Icon);
                return;
            }

            string stored = PlayerPrefs.GetString(HighlightModePrefKey, HighlightMode.Icon.ToString());
            if (Enum.TryParse(stored, true, out HighlightMode parsed))
                HighlightMode = parsed;
        }
        catch
        {
            HighlightMode = HighlightMode.Icon;
        }
    }

    /// <summary>
    /// Restores size, opacity, range, and color preferences within safe bounds.
    /// </summary>
    private void LoadVisualPreferences()
    {
        try
        {
            SizePercent = Mathf.Clamp(PlayerPrefs.GetFloat(SizePrefKey, DefaultSizePercent), 0f, 200f);
            OpacityPercent = Mathf.Clamp(PlayerPrefs.GetFloat(OpacityPrefKey, DefaultOpacityPercent), 0f, 100f);
            RangeBonusMeters = Mathf.Clamp(PlayerPrefs.GetFloat(RangePrefKey, DefaultRangeBonusMeters), RangeBonusMin, RangeBonusMax);

            string storedColor = PlayerPrefs.GetString(ColorPrefKey, DefaultColorHex);
            if (!TryParseColor(storedColor, out var color))
                color = ParseDefaultColor();
            UserColor = color;
        }
        catch
        {
            SizePercent = DefaultSizePercent;
            OpacityPercent = DefaultOpacityPercent;
            UserColor = ParseDefaultColor();
            RangeBonusMeters = DefaultRangeBonusMeters;
        }
    }

    /// <summary>
    /// Formats positive/negative meter offsets with explicit signs.
    /// </summary>
    private static string FormatMeters(float value) => value >= 0f ? $"+{value:0.0}m" : $"{value:0.0}m";

    private static Color ParseDefaultColor()
    {
        if (ColorUtility.TryParseHtmlString("#" + DefaultColorHex, out var color))
        {
            color.a = 1f;
            return color;
        }

        return new Color(0.8549f, 0.6470f, 0.1255f, 1f);
    }

    /// <summary>
    /// Writes the selected highlight mode back into PlayerPrefs.
    /// </summary>
    private void SaveHighlightPreference(HighlightMode mode)
    {
        try
        {
            PlayerPrefs.SetString(HighlightModePrefKey, mode.ToString());
            PlayerPrefs.Save();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Persists the current color choice as an RGB hex string.
    /// </summary>
    private void SaveColorPreference()
    {
        try
        {
            PlayerPrefs.SetString(ColorPrefKey, ColorUtility.ToHtmlStringRGB(UserColor));
            PlayerPrefs.Save();
        }
        catch
        {
            // ignored
        }
    }

    /// <summary>
    /// Helper that stores a float in PlayerPrefs while guarding against exceptions.
    /// </summary>
    private static void SaveFloat(string key, float value)
    {
        try
        {
            PlayerPrefs.SetFloat(key, value);
            PlayerPrefs.Save();
        }
        catch
        {
            // ignored
        }
    }
}
