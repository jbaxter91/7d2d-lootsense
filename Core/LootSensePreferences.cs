using System;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

internal sealed class LootSensePreferences
{
    private const string HighlightModePrefKey = "PMLootSense.HighlightMode";
    private const string SizePrefKey = "PMLootSense.SizePercent";
    private const string OpacityPrefKey = "PMLootSense.OpacityPercent";
    private const string ColorPrefKey = "PMLootSense.Color";
    private const string RangePrefKey = "PMLootSense.RangeBonus";

    private const float DefaultSizePercent = 100f;
    private const float DefaultOpacityPercent = 80f;
    private const string DefaultColorHex = "19FF19";
    private const float RangeBonusMin = -10f;
    private const float RangeBonusMax = 30f;

    private const float BoxScaleBase = 1.18f;
    private const float OutlineScaleBase = 1.045f;
    private const float IconBaseSize = 0.9f;

    public LootSensePreferences()
    {
        LoadHighlightPreference();
        LoadVisualPreferences();
    }

    public HighlightMode HighlightMode { get; private set; } = HighlightMode.Icon;
    public float SizePercent { get; private set; } = DefaultSizePercent;
    public float OpacityPercent { get; private set; } = DefaultOpacityPercent;
    public Color UserColor { get; private set; } = new(0.1f, 1f, 0.1f, 1f);
    public float RangeBonusMeters { get; private set; }

    public float SizeScale => Mathf.Clamp(SizePercent, 0f, 200f) / 100f;
    public float BoxScale => BoxScaleBase * Mathf.Max(SizeScale, 0f);
    public float OutlineScale => BoxScale * OutlineScaleBase;
    public float IconScale => IconBaseSize * Mathf.Max(SizeScale, 0f);
    public float Alpha => Mathf.Clamp01(OpacityPercent / 100f);
    public string ColorHex => ColorUtility.ToHtmlStringRGB(UserColor);

    public bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
    {
        mode = HighlightMode;
        message = null;

        if (string.IsNullOrEmpty(token))
        {
            message = "Highlight mode name missing.";
            return false;
        }

        if (token.Equals("solidbox", StringComparison.OrdinalIgnoreCase))
            token = "box";

        if (Enum.TryParse(token, true, out HighlightMode parsed))
        {
            if (HighlightMode != parsed)
            {
                HighlightMode = parsed;
                SaveHighlightPreference(parsed);
            }

            mode = parsed;
            return true;
        }

        string options = string.Join(", ", Enum.GetNames(typeof(HighlightMode)).Select(n => n.ToLowerInvariant()));
        message = $"Unknown highlight mode '{token}'. Options: {options}";
        return false;
    }

    public bool TrySetOpacity(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 100f, out float value, out message))
            return false;

        OpacityPercent = value;
        SaveFloat(OpacityPrefKey, OpacityPercent);
        message = $"Opacity set to {value:F0}%";
        return true;
    }

    public bool TrySetSize(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 200f, out float value, out message))
            return false;

        SizePercent = value;
        SaveFloat(SizePrefKey, SizePercent);
        message = $"Size set to {value:F0}%";
        return true;
    }

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

    public string BuildStatusSummary(float currentRadiusMeters)
    {
        string options = string.Join(", ", Enum.GetNames(typeof(HighlightMode)).Select(n => n.ToLowerInvariant()));
        return string.Format(CultureInfo.InvariantCulture,
            "mode={0} size={1:F0}% opacity={2:F0}% color=#{3} range={4} currentRange={5:F1}m options=[{6}]",
            HighlightMode.ToString().ToLowerInvariant(),
            SizePercent,
            OpacityPercent,
            ColorHex,
            FormatMeters(RangeBonusMeters),
            currentRadiusMeters,
            options);
    }

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

    private static string FormatMeters(float value) => value >= 0f ? $"+{value:0.0}m" : $"{value:0.0}m";

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

    private void LoadVisualPreferences()
    {
        try
        {
            SizePercent = Mathf.Clamp(PlayerPrefs.GetFloat(SizePrefKey, DefaultSizePercent), 0f, 200f);
            OpacityPercent = Mathf.Clamp(PlayerPrefs.GetFloat(OpacityPrefKey, DefaultOpacityPercent), 0f, 100f);
            RangeBonusMeters = Mathf.Clamp(PlayerPrefs.GetFloat(RangePrefKey, 0f), RangeBonusMin, RangeBonusMax);

            string storedColor = PlayerPrefs.GetString(ColorPrefKey, DefaultColorHex);
            if (!TryParseColor(storedColor, out var color))
                color = new Color(0.1f, 1f, 0.1f, 1f);
            UserColor = color;
        }
        catch
        {
            SizePercent = DefaultSizePercent;
            OpacityPercent = DefaultOpacityPercent;
            UserColor = new Color(0.1f, 1f, 0.1f, 1f);
            RangeBonusMeters = 0f;
        }
    }

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
