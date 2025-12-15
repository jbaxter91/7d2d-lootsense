using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

[HarmonyPatch(typeof(EntityPlayerLocal), "Update")]
public static class LootSense
{
    private const string PerkName = "perkPerceptionMastery";

    // Rank -> meters (index 0 unused)
    private static readonly float[] RankMeters = { 0f, 1f, 2f, 3f, 4f, 5f };

    private const float ScanIntervalSeconds = 0.30f;

    // Highlight look & feel
    private const float OutlineScaleBase = 1.045f;
    private const float BoxScaleBase = 1.18f;
    private const float IconBaseSize = 0.9f;
    private const float IconVerticalOffset = 0f;
    private const int EnumerableProbeLimit = 4;

    internal enum HighlightMode
    {
        Box,
        Icon
    }

    private static HighlightMode _highlightMode = HighlightMode.Icon;
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
    private static float _sizePercent = DefaultSizePercent;
    private static float _opacityPercent = DefaultOpacityPercent;
    private static Color _userColor = new Color(0.1f, 1f, 0.1f, 1f);
    private static float _rangeBonusMeters;

    static LootSense()
    {
        LoadHighlightPreference();
        LoadVisualPreferences();
    }

    private static readonly bool DebugMode = false;
    private static readonly bool VerboseLogging = false;
    private static readonly bool PositionTraceLogging = false;
    private const float OverlayTraceIntervalSeconds = 2f;

    private static float _nextDebugTime;
    private static float _nextScanTime;
    private static float _nextOverlayTraceTime;

    // World origin shift (7DTD re-centers world near 0,0 to avoid float drift)
    private static Vector3 _worldOriginOffset = Vector3.zero;

    // Active markers to draw (position + mesh data)
    private static Dictionary<Vector3i, LootMarker> _activeMarkers = new(new Vector3iComparer());
    private static Dictionary<Vector3i, LootMarker> _scratchMarkers = new(new Vector3iComparer());
    private static readonly List<Vector3i> _scanOffsets = new();
    private static int _scanOffsetsRadius;
    private static int _scanOffsetCursor;
    private const int MaxOffsetsPerScan = 1600;
    private const float MarkerTimeoutSeconds = 10f;
    private const float RangeGraceMeters = 1.5f;
    private const float MovementScanThresholdMeters = 0.35f;
    private const int ActiveRechecksPerScan = 256;
    private static readonly object _posLock = new();
    private static readonly object _scanGateLock = new();
    private static Vector3 _lastScanPosition;
    private static bool _hasLastScanPosition;

    // For one-time verbose logging per position
    private static readonly HashSet<Vector3i> _loggedPositions = new(new Vector3iComparer());
    private static readonly Queue<Vector3i> _recheckQueue = new();
    private static readonly HashSet<Vector3i> _recheckMembership = new(new Vector3iComparer());

    private static readonly Dictionary<Type, bool> _blockLootableCache = new();
    private static readonly Dictionary<string, FieldInfo> _fieldInfoCache = new();
    private static readonly Dictionary<string, PropertyInfo> _propertyInfoCache = new();
    private static readonly object _memberCacheLock = new();
    private const BindingFlags MemberBindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MethodInfo _miWorldIsChunkLoadedXZ;
    private static MethodInfo _miWorldGetChunkFromWorldPos;
    private static MethodInfo _miWorldGetChunkFromWorldPosXYZ;
    private const int ChunkCoordShift = 4; // 16-block chunks

    // Progression reflection cache
    private static MethodInfo _miGetPerkLevel;
    private static MethodInfo _miGetProgressionValue;

    // Material + mesh caches
    private static Material _fillMaterial;
    private static Material _outlineMaterial;
    private static readonly Dictionary<int, Mesh> _meshCache = new();
    private static readonly Dictionary<int, Vector3> _meshPivotCache = new();
    private static Mesh _fallbackCubeMesh;
    private static Vector3 _fallbackCubePivot;
    private static MethodInfo _miBlockRotation;
    private static readonly string[] MeshMethodCandidates = { "GetPreviewMesh", "GetRenderMesh", "GetModelMesh", "GetMesh" };

    // Icon resources
    private static Material _iconMaterial;
    private static Mesh _iconQuadMesh;
    private static Texture2D _iconTexture;
    private static Sprite _iconSprite;
    private static Texture2D _generatedIconTexture;
    private static bool _loggedMissingIcon;
    private const string IconSpriteName = "perkPerceptionMastery";
    private static readonly Vector2[] DefaultIconUVs =
    {
        new(0f, 0f),
        new(1f, 0f),
        new(0f, 1f),
        new(1f, 1f)
    };

    // Overlay singleton
    private static LootSenseOverlay _overlay;

    internal static HighlightMode CurrentMode => _highlightMode;

    internal static bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
    {
        mode = _highlightMode;
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
            if (_highlightMode != parsed)
            {
                _highlightMode = parsed;
                SaveHighlightPreference(parsed);
                if (DebugMode)
                    Debug.Log($"[PerceptionMasteryLootSense] Highlight mode switched to {parsed}.");
            }

            mode = parsed;
            return true;
        }

        string options = string.Join(", ", Enum.GetNames(typeof(HighlightMode)).Select(n => n.ToLowerInvariant()));
        message = $"Unknown highlight mode '{token}'. Options: {options}";
        return false;
    }

    internal static string GetHighlightModeSummary()
    {
        string options = string.Join(", ", Enum.GetNames(typeof(HighlightMode)).Select(n => n.ToLowerInvariant()));
        return $"mode={_highlightMode.ToString().ToLowerInvariant()} size={_sizePercent:F0}% opacity={_opacityPercent:F0}% color=#{GetColorHex()} range={FormatMeters(_rangeBonusMeters)} rank5={PreviewRadiusForRank(5):0.0}m options=[{options}]";
    }

    internal static string GetConfigDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine("LootSense configuration snapshot:");
        sb.AppendLine($"  mode={_highlightMode.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  sizePercent={_sizePercent:F1}");
        sb.AppendLine($"  opacityPercent={_opacityPercent:F1}");
        sb.AppendLine($"  colorHex=#{GetColorHex()}");
        sb.AppendLine($"  rangeBonusMeters={_rangeBonusMeters:F2}");
        for (int rank = 1; rank < RankMeters.Length; rank++)
        {
            sb.AppendLine($"  rank{rank}RadiusMeters={PreviewRadiusForRank(rank):F2}");
        }

        return sb.ToString();
    }

    internal static bool TrySetOpacity(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 100f, out float percent, out message))
            return false;

        _opacityPercent = percent;
        SaveOpacityPreference();
        ApplyVisualSettings();
        message = $"Opacity set to {percent:F0}%";
        return true;
    }

    internal static bool TrySetSize(string token, out string message)
    {
        if (!TryParsePercent(token, 0f, 200f, out float percent, out message))
            return false;

        _sizePercent = percent;
        SaveSizePreference();
        message = $"Size set to {percent:F0}%";
        return true;
    }

    internal static bool TrySetColor(string token, out string message)
    {
        if (!TryParseColorHex(token, out var color))
        {
            message = "Invalid color. Use hex formats like FF0000 or #FF0000.";
            return false;
        }

        _userColor = color;
        SaveColorPreference();
        ApplyVisualSettings();
        message = $"Color set to #{GetColorHex()}";
        return true;
    }

    internal static bool TryAdjustRange(string token, out string message)
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

        _rangeBonusMeters = Mathf.Clamp(_rangeBonusMeters + delta, RangeBonusMin, RangeBonusMax);
        SaveRangePreference();
        message = $"Range bonus now {FormatMeters(_rangeBonusMeters)} (rank5 total {PreviewRadiusForRank(5):0.0}m)";
        return true;
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

    private static void LoadHighlightPreference()
    {
        try
        {
            if (!PlayerPrefs.HasKey(HighlightModePrefKey))
            {
                SaveHighlightPreference(HighlightMode.Icon);
                return;
            }

            string stored = PlayerPrefs.GetString(HighlightModePrefKey, HighlightMode.Icon.ToString());
            if (stored.Equals("MeshOverlay", StringComparison.OrdinalIgnoreCase))
            {
                _highlightMode = HighlightMode.Icon;
                SaveHighlightPreference(_highlightMode);
                return;
            }

            if (stored.Equals("SolidBox", StringComparison.OrdinalIgnoreCase))
            {
                _highlightMode = HighlightMode.Box;
                SaveHighlightPreference(_highlightMode);
                return;
            }

            if (Enum.TryParse(stored, true, out HighlightMode parsed))
                _highlightMode = parsed;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to load highlight mode preference: {e.Message}");
            _highlightMode = HighlightMode.Icon;
        }
    }

    private static void SaveHighlightPreference(HighlightMode mode)
    {
        try
        {
            PlayerPrefs.SetString(HighlightModePrefKey, mode.ToString());
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to save highlight mode preference: {e.Message}");
        }
    }

    private static void LoadVisualPreferences()
    {
        try
        {
            _sizePercent = Mathf.Clamp(PlayerPrefs.GetFloat(SizePrefKey, DefaultSizePercent), 0f, 200f);
            _opacityPercent = Mathf.Clamp(PlayerPrefs.GetFloat(OpacityPrefKey, DefaultOpacityPercent), 0f, 100f);
            _rangeBonusMeters = Mathf.Clamp(PlayerPrefs.GetFloat(RangePrefKey, 0f), RangeBonusMin, RangeBonusMax);

            string storedColor = PlayerPrefs.GetString(ColorPrefKey, DefaultColorHex);
            if (!TryParseColorHex(storedColor, out _userColor))
                _userColor = new Color(0.1f, 1f, 0.1f, 1f);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to load visual prefs: {e.Message}");
            _sizePercent = DefaultSizePercent;
            _opacityPercent = DefaultOpacityPercent;
            _userColor = new Color(0.1f, 1f, 0.1f, 1f);
            _rangeBonusMeters = 0f;
        }
    }

    private static void SaveSizePreference()
    {
        try
        {
            PlayerPrefs.SetFloat(SizePrefKey, _sizePercent);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to save size preference: {e.Message}");
        }
    }

    private static void SaveOpacityPreference()
    {
        try
        {
            PlayerPrefs.SetFloat(OpacityPrefKey, _opacityPercent);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to save opacity preference: {e.Message}");
        }
    }

    private static void SaveColorPreference()
    {
        try
        {
            PlayerPrefs.SetString(ColorPrefKey, ColorUtility.ToHtmlStringRGB(_userColor));
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to save color preference: {e.Message}");
        }
    }

    private static void SaveRangePreference()
    {
        try
        {
            PlayerPrefs.SetFloat(RangePrefKey, _rangeBonusMeters);
            PlayerPrefs.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to save range preference: {e.Message}");
        }
    }

    private static bool TryParseColorHex(string token, out Color color)
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

    private static float SizeScale => Mathf.Clamp(_sizePercent, 0f, 200f) / 100f;
    private static float CurrentBoxScale => BoxScaleBase * Mathf.Max(SizeScale, 0f);
    private static float CurrentOutlineScale => CurrentBoxScale * OutlineScaleBase;
    private static float CurrentIconScale => IconBaseSize * Mathf.Max(SizeScale, 0f);

    private static float CurrentAlpha => Mathf.Clamp01(_opacityPercent / 100f);

    private static void ApplyVisualSettings()
    {
        var alpha = CurrentAlpha;
        var fillColor = new Color(_userColor.r, _userColor.g, _userColor.b, alpha);
        var outlineColor = new Color(_userColor.r, _userColor.g, _userColor.b, Mathf.Clamp01(alpha * 1.35f));

        if (_fillMaterial != null)
        {
            _fillMaterial.SetColor("_Color", fillColor);
            _fillMaterial.EnableKeyword("_EMISSION");
            _fillMaterial.SetColor("_EmissionColor", fillColor * 2.2f);
        }

        if (_outlineMaterial != null)
        {
            _outlineMaterial.SetColor("_Color", outlineColor);
        }

        if (_iconMaterial != null)
        {
            _iconMaterial.color = fillColor;
        }
    }

    private static string GetColorHex() => ColorUtility.ToHtmlStringRGB(_userColor);

    private static string FormatMeters(float value) => value >= 0f ? $"+{value:0.0}m" : $"{value:0.0}m";

    private static float GetBaseRadiusForRank(int rank)
    {
        int idx = Mathf.Clamp(rank, 0, RankMeters.Length - 1);
        return RankMeters[idx];
    }

    private static float ApplyRangeBonus(float baseRadius)
    {
        return Mathf.Max(0f, baseRadius + _rangeBonusMeters);
    }

    private static float PreviewRadiusForRank(int rank) => ApplyRangeBonus(GetBaseRadiusForRank(rank));

    [HarmonyPostfix]
    public static void Postfix(EntityPlayerLocal __instance)
    {
        try
        {
            if (__instance == null || __instance.IsDead())
            {
                ClearAll();
                return;
            }

            EnsureOverlay();

            if (Time.time < _nextScanTime) return;
            _nextScanTime = Time.time + ScanIntervalSeconds;

            int rank = GetPerkRank(__instance);

            if (DebugMode && Time.time >= _nextDebugTime)
            {
                _nextDebugTime = Time.time + 3f;
                Debug.Log($"[PerceptionMasteryLootSense] Tick OK. perk={PerkName} rank={rank} active={CountActive()}");
            }

            if (rank <= 0)
            {
                ClearAll();
                return;
            }

            float radius = ApplyRangeBonus(GetBaseRadiusForRank(rank));
            if (radius <= 0.01f)
            {
                ClearAll();
                return;
            }

            var playerPos = __instance.GetPosition();
            bool shouldScan = ShouldPerformFullScan(playerPos);
            var world = __instance.world;

            if (shouldScan)
            {
                RecordScanPosition(playerPos);
                ScanAndMark(__instance, radius);
            }
            else if (world != null)
            {
                RevalidateActiveMarkers(world, playerPos, radius, Time.time);
                PruneStaleMarkers(Time.time);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[PerceptionMasteryLootSense] ERROR in Update postfix: {e}");
            _nextScanTime = Time.time + 1.0f;
        }
    }

    private static bool TryGetRenderGeometry(LootMarker marker, out Mesh mesh, out Vector3 pivot, out Vector3 localCenter, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        mesh = null;
        pivot = Vector3.zero;
        localCenter = Vector3.zero;

        if (_highlightMode == HighlightMode.Box)
        {
            mesh = EnsureFallbackCubeMesh();
            if (mesh == null) return false;
            pivot = _fallbackCubePivot;
            localCenter = mesh.bounds.center + pivot;
            return true;
        }

        if (marker.Mesh != null)
        {
            mesh = marker.Mesh;
            pivot = marker.PivotOffset;
            localCenter = marker.LocalCenter;
            rotation = marker.Rotation;
            return true;
        }

        mesh = EnsureFallbackCubeMesh();
        if (mesh == null) return false;
        pivot = _fallbackCubePivot;
        localCenter = mesh.bounds.center + pivot;
        return true;
    }

    private static Matrix4x4 BuildFillMatrix(Vector3 renderBase, Quaternion rotation, Vector3 pivot, Vector3 localCenter, float scale)
    {
        var baseMatrix = Matrix4x4.TRS(renderBase, rotation, Vector3.one)
                        * Matrix4x4.Translate(pivot);

        if (Mathf.Abs(scale - 1f) < 0.0001f)
            return baseMatrix;

        return baseMatrix
               * Matrix4x4.Translate(localCenter)
               * Matrix4x4.Scale(Vector3.one * scale)
               * Matrix4x4.Translate(-localCenter);
    }

    private static int CountActive()
    {
        lock (_posLock) return _activeMarkers.Count;
    }

    private static void ScanAndMark(EntityPlayerLocal player, float radius)
    {
        var world = player.world;
        if (world == null) return;

        Vector3 origin = player.GetPosition();
        var center = new Vector3i(
            Mathf.FloorToInt(origin.x),
            Mathf.FloorToInt(origin.y),
            Mathf.FloorToInt(origin.z)
        );

        int r = Mathf.CeilToInt(radius);
        if (r <= 0)
        {
            ClearAll();
            return;
        }

        EnsureScanOffsets(r);

        int totalOffsets = _scanOffsets.Count;
        if (totalOffsets == 0)
        {
            ClearAll();
            return;
        }

        int batchSize = Mathf.Min(totalOffsets, MaxOffsetsPerScan);
        float now = Time.time;

        var updates = _scratchMarkers;
        updates.Clear();

        for (int i = 0; i < batchSize; i++)
        {
            var offset = _scanOffsets[_scanOffsetCursor];
            _scanOffsetCursor++;
            if (_scanOffsetCursor >= totalOffsets)
                _scanOffsetCursor = 0;

            var pos = new Vector3i(center.x + offset.x, center.y + offset.y, center.z + offset.z);

            if (!IsLootableAndUnopened(world, pos, out string verboseState, out BlockValue blockValue, out object visualSource))
                continue;

            var marker = BuildLootMarker(pos, blockValue, visualSource, now);
            updates[pos] = marker;

            if (VerboseLogging && !_loggedPositions.Contains(pos))
            {
                if (!string.IsNullOrEmpty(verboseState))
                    Debug.Log(verboseState);
                _loggedPositions.Add(pos);
            }
        }

        if (updates.Count > 0)
        {
            lock (_posLock)
            {
                foreach (var kvp in updates)
                    _activeMarkers[kvp.Key] = kvp.Value;
            }

            foreach (var key in updates.Keys)
                EnqueueForRecheck(key);
        }

        updates.Clear();
        RevalidateActiveMarkers(world, origin, radius, now);
        PruneStaleMarkers(now);
    }

    private static bool ShouldPerformFullScan(Vector3 playerPosition)
    {
        lock (_scanGateLock)
        {
            if (!_hasLastScanPosition)
                return true;

            float thresholdSqr = MovementScanThresholdMeters * MovementScanThresholdMeters;
            float distanceSqr = (playerPosition - _lastScanPosition).sqrMagnitude;
            return distanceSqr >= thresholdSqr;
        }
    }

    private static void RecordScanPosition(Vector3 playerPosition)
    {
        lock (_scanGateLock)
        {
            _lastScanPosition = playerPosition;
            _hasLastScanPosition = true;
        }
    }

    private static void EnqueueForRecheck(Vector3i pos)
    {
        if (_recheckMembership.Add(pos))
            _recheckQueue.Enqueue(pos);
    }

    private static void RevalidateActiveMarkers(World world, Vector3 playerPosition, float activeRadius, float now)
    {
        if (world == null || ActiveRechecksPerScan <= 0)
            return;

        int checks = Mathf.Min(ActiveRechecksPerScan, _recheckQueue.Count);
        int processed = 0;
        float radiusLimit = Mathf.Max(0f, activeRadius) + RangeGraceMeters;
        float radiusLimitSqr = radiusLimit * radiusLimit;

        while (_recheckQueue.Count > 0 && processed < checks)
        {
            var pos = _recheckQueue.Dequeue();
            _recheckMembership.Remove(pos);
            processed++;

            var markerCenter = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
            float distanceSqr = (markerCenter - playerPosition).sqrMagnitude;
            if (distanceSqr > radiusLimitSqr)
            {
                lock (_posLock)
                    _activeMarkers.Remove(pos);
                _loggedPositions.Remove(pos);
                continue;
            }

            LootMarker existing;
            bool exists;
            lock (_posLock)
                exists = _activeMarkers.TryGetValue(pos, out existing);

            if (!exists)
                continue;

            if (!IsLootableAndUnopened(world, pos, out _, out BlockValue blockValue, out object visualSource))
            {
                lock (_posLock)
                    _activeMarkers.Remove(pos);
                _loggedPositions.Remove(pos);
                continue;
            }

            var refreshed = BuildLootMarker(pos, blockValue, visualSource, now);
            lock (_posLock)
                _activeMarkers[pos] = refreshed;

            EnqueueForRecheck(pos);
        }
    }

    private static void EnsureScanOffsets(int radius)
    {
        if (radius == _scanOffsetsRadius && _scanOffsets.Count > 0)
            return;

        if (radius <= 0)
        {
            _scanOffsets.Clear();
            _scanOffsetsRadius = 0;
            _scanOffsetCursor = 0;
            return;
        }

        _scanOffsets.Clear();
        _scanOffsetsRadius = radius;
        _scanOffsetCursor = 0;

        int r2 = radius * radius;

        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        for (int dz = -radius; dz <= radius; dz++)
        {
            int dist2 = (dx * dx) + (dy * dy) + (dz * dz);
            if (dist2 <= r2)
                _scanOffsets.Add(new Vector3i(dx, dy, dz));
        }

        _scanOffsets.Sort((a, b) =>
        {
            int da = (a.x * a.x) + (a.y * a.y) + (a.z * a.z);
            int db = (b.x * b.x) + (b.y * b.y) + (b.z * b.z);
            return da.CompareTo(db);
        });
    }

    private static void PruneStaleMarkers(float now)
    {
        List<Vector3i> toRemove = null;

        lock (_posLock)
        {
            foreach (var kvp in _activeMarkers)
            {
                if (now - kvp.Value.LastSeenTime <= MarkerTimeoutSeconds)
                    continue;

                toRemove ??= new List<Vector3i>();
                toRemove.Add(kvp.Key);
            }

            if (toRemove != null)
            {
                foreach (var pos in toRemove)
                {
                    _activeMarkers.Remove(pos);
                    _loggedPositions.Remove(pos);
                    _recheckMembership.Remove(pos);
                }
            }
        }
    }

    // =========================
    // Mesh + marker helpers
    // =========================

    private static LootMarker BuildLootMarker(Vector3i pos, BlockValue blockValue, object visualSource, float timestamp)
    {
        Mesh mesh;
        Vector3 pivot;

        bool hasCustomMesh = TryResolveMesh(blockValue, visualSource, out mesh, out pivot);
        if (!hasCustomMesh)
        {
            mesh = EnsureFallbackCubeMesh();
            pivot = _fallbackCubePivot;
        }

        if (mesh == null)
        {
            mesh = EnsureFallbackCubeMesh();
            pivot = _fallbackCubePivot;
        }

        var rotation = ResolveRotation(blockValue);
        var localCenter = mesh != null ? mesh.bounds.center + pivot : new Vector3(0.5f, 0.5f, 0.5f);

        return new LootMarker(pos, mesh, pivot, localCenter, rotation, timestamp);
    }

    private static bool TryResolveMesh(BlockValue blockValue, object visualSource, out Mesh mesh, out Vector3 pivot)
    {
        int cacheKey = GetMeshCacheKey(blockValue);
        if (_meshCache.TryGetValue(cacheKey, out mesh))
        {
            pivot = _meshPivotCache.TryGetValue(cacheKey, out var cachedPivot) ? cachedPivot : Vector3.zero;
            return mesh != null;
        }

        if (visualSource != null && TryExtractMeshFromMembers(visualSource, out mesh))
        {
            pivot = mesh != null ? -mesh.bounds.min : Vector3.zero;
            _meshCache[cacheKey] = mesh;
            _meshPivotCache[cacheKey] = pivot;
            return mesh != null;
        }

        mesh = BuildMesh(blockValue);
        if (mesh != null)
        {
            pivot = -mesh.bounds.min;
            _meshCache[cacheKey] = mesh;
            _meshPivotCache[cacheKey] = pivot;
            return true;
        }

        pivot = Vector3.zero;
        _meshCache[cacheKey] = null;
        _meshPivotCache[cacheKey] = pivot;
        return false;
    }

    private static Mesh BuildMesh(BlockValue blockValue)
    {
        var block = blockValue.Block;
        if (block == null) return null;

        if (TryExtractMesh(block.shape, block, blockValue, out var mesh))
            return mesh;

        if (TryExtractMesh(block, block, blockValue, out mesh))
            return mesh;

        return null;
    }

    private static bool TryExtractMesh(object provider, Block block, BlockValue blockValue, out Mesh mesh)
    {
        mesh = null;
        if (provider == null) return false;

        if (TryExtractMeshFromMembers(provider, out mesh))
            return true;

        var type = provider.GetType();
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var method in methods)
        {
            if (!MeshMethodCandidates.Contains(method.Name))
                continue;

            if (!typeof(Mesh).IsAssignableFrom(method.ReturnType))
                continue;

            var args = BuildMeshArgs(method.GetParameters(), block, blockValue);
            try
            {
                var result = method.Invoke(provider, args);
                if (result is Mesh m && m != null)
                {
                    mesh = m;
                    return true;
                }
            }
            catch
            {
                // Ignore failures — we'll fall back to a cube mesh
            }
        }

        return false;
    }

    private static bool TryExtractMeshFromMembers(object provider, out Mesh mesh)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = provider.GetType();

        foreach (var field in type.GetFields(Flags))
        {
            object value;
            try { value = field.GetValue(provider); }
            catch { continue; }

            if (TryConvertObjectToMesh(field.Name, value, out mesh))
                return true;
        }

        foreach (var prop in type.GetProperties(Flags))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            object value;
            try { value = prop.GetValue(provider, null); }
            catch { continue; }

            if (TryConvertObjectToMesh(prop.Name, value, out mesh))
                return true;
        }

        mesh = null;
        return false;
    }

    private static bool TryConvertObjectToMesh(string memberName, object candidate, out Mesh mesh)
    {
        mesh = null;
        if (candidate == null) return false;

        if (candidate is Mesh readyMesh && IsRenderableMesh(readyMesh))
        {
            mesh = readyMesh;
            return true;
        }

        if (candidate is Mesh[] meshArray)
        {
            foreach (var m in meshArray)
            {
                if (m == null) continue;
                if (IsRenderableMesh(m))
                {
                    mesh = m;
                    return true;
                }
            }
        }
        else if (candidate is GameObject go)
        {
            var meshFilter = go.GetComponentInChildren<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                mesh = meshFilter.sharedMesh;
                return true;
            }

            var skinned = go.GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinned != null && skinned.sharedMesh != null)
            {
                mesh = skinned.sharedMesh;
                return true;
            }
        }
        else if (candidate is Component component)
        {
            if (component.gameObject != null && TryConvertObjectToMesh(memberName, component.gameObject, out mesh))
                return true;
        }
        else if (candidate is IEnumerable enumerable && candidate is not string)
        {
            int scanned = 0;
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                if (TryConvertObjectToMesh(memberName, item, out mesh))
                    return true;

                if (++scanned >= EnumerableProbeLimit)
                    break;
            }
        }
        else
        {
            var candidateType = candidate.GetType();
            if (candidateType.Name.IndexOf("MeshDescription", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryBuildMeshFromDescription(candidate, memberName, out mesh))
                    return true;
            }
        }

        return false;
    }

    private static bool IsRenderableMesh(Mesh mesh)
    {
        return mesh != null && mesh.vertexCount > 0 && mesh.triangles != null && mesh.triangles.Length > 0;
    }

    private static bool TryBuildMeshFromDescription(object desc, string memberName, out Mesh mesh)
    {
        mesh = null;
        if (desc == null) return false;

        var type = desc.GetType();
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        Vector3[] vertices = null;
        Vector3[] normals = null;
        Vector2[] uvs = null;
        int[] indices = null;

        void Consume(string name, object value)
        {
            if (value == null) return;

            if (value is Vector3[] vec3 && vec3.Length > 0)
            {
                if (name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (normals == null)
                        normals = vec3;
                }
                else
                {
                    if (vertices == null)
                        vertices = vec3;
                }
            }
            else if (value is Vector2[] vec2 && vec2.Length > 0)
            {
                if (uvs == null)
                    uvs = vec2;
            }
            else if (value is int[] ints && ints.Length > 0)
            {
                if (indices == null)
                    indices = ints;
            }
        }

        foreach (var field in type.GetFields(flags))
        {
            object value;
            try { value = field.GetValue(desc); }
            catch { continue; }
            Consume(field.Name, value);
        }

        foreach (var prop in type.GetProperties(flags))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
            object value;
            try { value = prop.GetValue(desc, null); }
            catch { continue; }
            Consume(prop.Name, value);
        }

        if (vertices == null || indices == null)
            return false;

        try
        {
            mesh = new Mesh
            {
                name = $"PMLootSense_{memberName}_MeshDesc"
            };

            mesh.vertices = vertices;
            mesh.triangles = indices;

            if (uvs != null && uvs.Length == vertices.Length)
                mesh.uv = uvs;

            if (normals != null && normals.Length == vertices.Length)
                mesh.normals = normals;
            else
                mesh.RecalculateNormals();

            mesh.RecalculateBounds();
            return true;
        }
        catch
        {
            mesh = null;
            return false;
        }
    }

    private static object[] BuildMeshArgs(ParameterInfo[] parameters, Block block, BlockValue blockValue)
    {
        var args = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.ParameterType;
            bool isByRef = paramType.IsByRef;
            var targetType = isByRef ? paramType.GetElementType() : paramType;

            if (targetType == typeof(BlockValue))
            {
                args[i] = blockValue;
            }
            else if (targetType == typeof(Block))
            {
                args[i] = block;
            }
            else if (targetType == typeof(bool))
            {
                args[i] = false;
            }
            else if (targetType == typeof(int))
            {
                args[i] = blockValue.type;
            }
            else if (targetType == typeof(float))
            {
                args[i] = 0f;
            }
            else if (targetType == typeof(Vector3))
            {
                args[i] = Vector3.zero;
            }
            else if (targetType == typeof(Vector3i))
            {
                args[i] = new Vector3i(0, 0, 0);
            }
            else if (targetType == typeof(Quaternion))
            {
                args[i] = Quaternion.identity;
            }
            else if (targetType == typeof(Matrix4x4))
            {
                args[i] = Matrix4x4.identity;
            }
            else if (targetType != null && targetType.IsEnum)
            {
                args[i] = Activator.CreateInstance(targetType);
            }
            else if (targetType != null && targetType.IsValueType)
            {
                args[i] = Activator.CreateInstance(targetType);
            }
            else
            {
                args[i] = null;
            }
        }

        return args;
    }

    private static int GetMeshCacheKey(BlockValue blockValue) => blockValue.GetHashCode();

    private static Mesh EnsureFallbackCubeMesh()
    {
        if (_fallbackCubeMesh != null) return _fallbackCubeMesh;

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = cube.GetComponent<MeshFilter>();
        if (meshFilter != null)
        {
            _fallbackCubeMesh = UnityEngine.Object.Instantiate(meshFilter.sharedMesh);
            _fallbackCubeMesh.name = "PMLootSenseFallbackCube";
            _fallbackCubePivot = -_fallbackCubeMesh.bounds.min;
        }

        UnityEngine.Object.Destroy(cube);
        return _fallbackCubeMesh;
    }

    private static Quaternion ResolveRotation(BlockValue blockValue)
    {
        try
        {
            _miBlockRotation ??= FindRotationMethod();
            if (_miBlockRotation != null)
            {
                var parameters = _miBlockRotation.GetParameters();
                var args = new object[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    bool isByRef = paramType.IsByRef;
                    var targetType = isByRef ? paramType.GetElementType() : paramType;

                    if (targetType == typeof(BlockValue))
                    {
                        args[i] = blockValue;
                    }
                    else if (targetType == typeof(bool))
                    {
                        args[i] = false;
                    }
                    else if (targetType == typeof(int))
                    {
                        args[i] = blockValue.type;
                    }
                    else if (targetType != null && targetType.IsValueType)
                    {
                        args[i] = Activator.CreateInstance(targetType);
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                var result = _miBlockRotation.Invoke(null, args);
                if (result is Quaternion q)
                    return q;
            }
        }
        catch
        {
            // ignored — fall back to identity rotation
        }

        return Quaternion.identity;
    }

    private static MethodInfo FindRotationMethod()
    {
        try
        {
            var asm = typeof(BlockValue).Assembly;
            foreach (var type in asm.GetTypes())
            {
                if (type.Name.IndexOf("Rotation", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.ReturnType != typeof(Quaternion))
                        continue;

                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                        continue;

                    var first = parameters[0].ParameterType;
                    if (first == typeof(BlockValue) || first == typeof(BlockValue).MakeByRefType())
                        return method;
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    // =========================
    // Overlay renderer (draws solid translucent boxes through walls)
    // =========================

    private static void EnsureOverlay()
    {
        if (_overlay != null) return;

        var go = new GameObject("PerceptionMasteryLootSenseOverlay");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _overlay = go.AddComponent<LootSenseOverlay>();

        if (DebugMode)
            Debug.Log("[PerceptionMasteryLootSense] Overlay renderer created.");
    }

    private class LootSenseOverlay : MonoBehaviour
    {
        private static float _nextPing;
        private bool _srpActive;

        private void OnEnable()
        {
            _srpActive = GraphicsSettings.renderPipelineAsset != null;
            Camera.onPostRender += OnPostRenderBuiltin;
            RenderPipelineManager.endCameraRendering += OnEndCameraRenderingSRP;

            if (DebugMode)
                Debug.Log("[PerceptionMasteryLootSense] Overlay enabled (camera callbacks).");
        }

        private void OnDisable()
        {
            Camera.onPostRender -= OnPostRenderBuiltin;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRenderingSRP;
        }

        private void OnPostRenderBuiltin(Camera cam)
        {
            if (_srpActive)
                return;
            DrawForCamera(cam);
        }

        private void OnEndCameraRenderingSRP(ScriptableRenderContext ctx, Camera cam)
        {
            DrawForCamera(cam);
        }

        private static bool RefreshWorldOriginOffset(Camera cam)
        {
            try
            {
                var gm = GameManager.Instance;
                var world = gm?.World;
                var player = world?.GetPrimaryPlayer() as EntityPlayerLocal;
                if (player == null) return false;

                Transform playerTransform = player.transform;
                if (playerTransform == null) return false;

                Vector3 worldPos = player.GetPosition();
                Vector3 localPos = playerTransform.position;
                _worldOriginOffset = worldPos - localPos;
                return true;
            }
            catch (Exception e)
            {
                if (DebugMode && PositionTraceLogging)
                    Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to refresh world origin offset: {e.Message}");
                return false;
            }
        }

        private void DrawForCamera(Camera cam)
        {
            try
            {
                if (cam == null) return;

                if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.VR)
                    return;

                // Skip UI/preview cameras
                var n = (cam.name ?? "").ToLowerInvariant();
                if (n.Contains("ui") || n.Contains("preview")) return;

                // Optional “is draw running?” ping (every 5s)
                if (DebugMode && Time.time >= _nextPing)
                {
                    _nextPing = Time.time + 5f;
                    Debug.Log($"[PerceptionMasteryLootSense] Draw callback OK on camera '{cam.name}'");
                }

                bool iconMode = _highlightMode == HighlightMode.Icon;

                if (iconMode)
                {
                    if (!EnsureIconResources())
                        return;
                }
                else
                {
                    EnsureHighlightMaterials();
                    if (_fillMaterial == null || _outlineMaterial == null)
                        return;
                }

                LootMarker[] snapshot;
                lock (_posLock)
                    snapshot = _activeMarkers.Values.ToArray();

                if (snapshot.Length == 0) return;

                RefreshWorldOriginOffset(cam);
                Vector3 originOffset = _worldOriginOffset;

                if (DebugMode && PositionTraceLogging && Time.time >= _nextOverlayTraceTime)
                {
                    _nextOverlayTraceTime = Time.time + OverlayTraceIntervalSeconds;
                    var sb = new StringBuilder();
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "[PerceptionMasteryLootSense] Overlay trace cam='{0}' camPos=({1:F2}, {2:F2}, {3:F2}) targets={4}",
                        cam.name,
                        cam.transform.position.x,
                        cam.transform.position.y,
                        cam.transform.position.z,
                        snapshot.Length);

                    int inspect = Math.Min(snapshot.Length, 4);
                    for (int i = 0; i < inspect; i++)
                    {
                        var marker = snapshot[i];
                        var pos = marker.Position;
                        var worldCenter = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
                        var renderCenter = worldCenter - originOffset;
                        var screen = cam.WorldToScreenPoint(renderCenter);
                        sb.AppendFormat(CultureInfo.InvariantCulture,
                            " | idx{0} world=({1:F3}, {2:F3}, {3:F3}) render=({4:F3}, {5:F3}, {6:F3}) screen=({7:F1}, {8:F1}, {9:F1})",
                            i,
                            worldCenter.x,
                            worldCenter.y,
                            worldCenter.z,
                            renderCenter.x,
                            renderCenter.y,
                            renderCenter.z,
                            screen.x,
                            screen.y,
                            screen.z);
                    }

                    Debug.Log(sb.ToString());
                }

                if (iconMode)
                {
                    GL.Clear(true, false, Color.black);
                }

                foreach (var marker in snapshot)
                {
                    if (iconMode)
                    {
                        DrawIconForMarker(marker, cam, originOffset);
                    }
                    else
                    {
                        DrawBoxForMarker(marker, originOffset);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PerceptionMasteryLootSense] Overlay draw error: {e.Message}");
            }
        }
    }

    private static void DrawIconForMarker(LootMarker marker, Camera cam, Vector3 originOffset)
    {
        if (_iconMaterial == null || _iconQuadMesh == null)
            return;

        float scale = CurrentIconScale;
        if (scale <= 0f)
            return;

        var worldCenter = new Vector3(marker.Position.x + 0.5f, marker.Position.y + 0.5f, marker.Position.z + 0.5f);
        var renderCenter = worldCenter - originOffset + new Vector3(0f, IconVerticalOffset, 0f);

        var camTransform = cam.transform;
        Vector3 forward = renderCenter - camTransform.position;
        if (forward.sqrMagnitude < 0.0001f)
            forward = camTransform.forward;

        var rotation = Quaternion.LookRotation(forward, camTransform.up);
        var scaleVec = new Vector3(scale, scale, scale);
        var matrix = Matrix4x4.TRS(renderCenter, rotation, scaleVec);

        if (_iconMaterial.SetPass(0))
            Graphics.DrawMeshNow(_iconQuadMesh, matrix);
    }

    private static void DrawBoxForMarker(LootMarker marker, Vector3 originOffset)
    {
        var cube = EnsureFallbackCubeMesh();
        if (cube == null || _fillMaterial == null || _outlineMaterial == null)
            return;

        float fillScale = Mathf.Max(0f, CurrentBoxScale);
        if (fillScale <= 0f)
            return;

        var worldCenter = new Vector3(marker.Position.x + 0.5f, marker.Position.y + 0.5f, marker.Position.z + 0.5f);
        var renderCenter = worldCenter - originOffset;

        var fillMatrix = Matrix4x4.TRS(renderCenter, Quaternion.identity, Vector3.one * fillScale);
        if (_fillMaterial.SetPass(0))
            Graphics.DrawMeshNow(cube, fillMatrix);

        var outlineScale = Mathf.Max(fillScale, CurrentOutlineScale);
        var outlineMatrix = Matrix4x4.TRS(renderCenter, Quaternion.identity, Vector3.one * outlineScale);
        if (_outlineMaterial.SetPass(0))
            Graphics.DrawMeshNow(cube, outlineMatrix);
    }

    private static void EnsureHighlightMaterials()
    {
        try
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                Debug.LogWarning("[PerceptionMasteryLootSense] Hidden/Internal-Colored not found. Cannot draw highlight overlay.");
                return;
            }

            if (_fillMaterial == null)
            {
                _fillMaterial = new Material(shader)
                {
                    name = "PMLootSenseFillMaterial",
                    hideFlags = HideFlags.HideAndDontSave,
                    renderQueue = 5000
                };

                ConfigureHighlightMaterial(_fillMaterial);
            }

            if (_outlineMaterial == null)
            {
                _outlineMaterial = new Material(shader)
                {
                    name = "PMLootSenseOutlineMaterial",
                    hideFlags = HideFlags.HideAndDontSave,
                    renderQueue = 5001
                };

                ConfigureHighlightMaterial(_outlineMaterial);
                _outlineMaterial.SetInt("_Cull", (int)CullMode.Front);
            }

            ApplyVisualSettings();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to create highlight materials: {e.Message}");
            _fillMaterial = null;
            _outlineMaterial = null;
        }
    }

    private static void ConfigureHighlightMaterial(Material mat)
    {
        if (mat == null) return;

        mat.SetInt("_ZWrite", 0);
        mat.SetInt("_ZTest", (int)CompareFunction.Always);
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_Cull", (int)CullMode.Off);
    }

    private static bool EnsureIconResources()
    {
        try
        {
            if (_iconQuadMesh == null)
                _iconQuadMesh = BuildIconQuadMesh();

            if (_iconSprite == null)
                _iconSprite = LocatePerceptionIconSprite();

            if (_iconTexture == null)
                _iconTexture = _iconSprite?.texture ?? LocatePerceptionIconTextureFallback();

            Shader shader = Shader.Find("Unlit/Transparent")
                              ?? Shader.Find("Sprites/Default")
                              ?? Shader.Find("GUI/Text Shader")
                              ?? Shader.Find("Hidden/Internal-Colored");
            if (_iconMaterial == null)
            {
                if (shader == null)
                {
                    Debug.LogWarning("[PerceptionMasteryLootSense] Unable to find a shader for icon rendering.");
                    return false;
                }

                _iconMaterial = new Material(shader)
                {
                    name = "PMLootSenseIconMaterial",
                    hideFlags = HideFlags.HideAndDontSave,
                    renderQueue = 5002
                };

                _iconMaterial.SetInt("_ZWrite", 0);
                _iconMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                _iconMaterial.SetInt("_Cull", (int)CullMode.Off);
                _iconMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _iconMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            }

            if (_iconMaterial != null)
                _iconMaterial.mainTexture = _iconTexture ?? Texture2D.whiteTexture;

            ApplyIconUVs();
            ApplyVisualSettings();
            return _iconMaterial != null && _iconQuadMesh != null;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to prepare icon resources: {e.Message}");
            return false;
        }
    }

    private static Mesh BuildIconQuadMesh()
    {
        var mesh = new Mesh
        {
            name = "PMLootSenseIconQuad"
        };

        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f)
        };

        mesh.uv = (Vector2[])DefaultIconUVs.Clone();

        mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Sprite LocatePerceptionIconSprite()
    {
        try
        {
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            foreach (var sprite in sprites)
            {
                if (sprite == null) continue;
                if (sprite.name.Equals(IconSpriteName, StringComparison.OrdinalIgnoreCase))
                    return sprite;
            }
        }
        catch (Exception e)
        {
            if (!_loggedMissingIcon)
            {
                _loggedMissingIcon = true;
                Debug.LogWarning($"[PerceptionMasteryLootSense] Error locating icon sprite: {e.Message}");
            }
        }

        if (!_loggedMissingIcon)
        {
            _loggedMissingIcon = true;
            Debug.LogWarning($"[PerceptionMasteryLootSense] Could not locate sprite '{IconSpriteName}'. Using fallback texture.");
        }

        return null;
    }

    private static Texture2D LocatePerceptionIconTextureFallback()
    {
        try
        {
            var textures = Resources.FindObjectsOfTypeAll<Texture2D>();
            foreach (var tex in textures)
            {
                if (tex != null && tex.name.Equals(IconSpriteName, StringComparison.OrdinalIgnoreCase))
                    return tex;
            }
        }
        catch
        {
            // ignore — will fall back to generated texture
        }

        return EnsureGeneratedIconTexture();
    }

    private static void ApplyIconUVs()
    {
        if (_iconQuadMesh == null)
            return;

        if (_iconSprite != null && _iconSprite.texture != null)
        {
            var tex = _iconSprite.texture;
            var rect = _iconSprite.textureRect;
            float invW = tex.width > 0 ? 1f / tex.width : 0f;
            float invH = tex.height > 0 ? 1f / tex.height : 0f;

            float xMin = rect.xMin * invW;
            float xMax = rect.xMax * invW;
            float yMin = rect.yMin * invH;
            float yMax = rect.yMax * invH;

            var uv = new[]
            {
                new Vector2(xMin, yMin),
                new Vector2(xMax, yMin),
                new Vector2(xMin, yMax),
                new Vector2(xMax, yMax)
            };

            _iconQuadMesh.uv = uv;
        }
        else
        {
            _iconQuadMesh.uv = (Vector2[])DefaultIconUVs.Clone();
        }
    }

    private static Texture2D EnsureGeneratedIconTexture()
    {
        if (_generatedIconTexture != null)
            return _generatedIconTexture;

        const int size = 96;
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, false)
        {
            name = "PMLootSenseEye",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        // Draw perception-style eye: outer almond + inner pupil + lids
        Color[] pixels = new Color[size * size];
        Color transparent = new Color(0f, 0f, 0f, 0f);
        float half = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x - half) / half;
                float ny = (y - half) / half;

                // Almond outline approximated by squashed circle
                float ellipse = (nx * nx) / (0.95f * 0.95f) + (ny * ny) / (0.55f * 0.55f);
                Color c = transparent;

                if (ellipse <= 1f)
                {
                    c = Color.white;
                    float iris = nx * nx + ny * ny;

                    if (iris <= 0.38f)
                        c = new Color(0.92f, 0.92f, 0.92f, 1f);

                    if (iris <= 0.18f)
                        c = Color.white;

                    if (iris <= 0.12f)
                        c = new Color(0.1f, 0.1f, 0.1f, 1f);

                    if (iris <= 0.06f)
                        c = Color.white;
                }

                pixels[y * size + x] = c;
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        _generatedIconTexture = tex;
        return tex;
    }

    private static void ClearAll()
    {
        lock (_posLock)
            _activeMarkers.Clear();

        _loggedPositions.Clear();
        _scratchMarkers.Clear();
        _scanOffsets.Clear();
        _scanOffsetsRadius = 0;
        _scanOffsetCursor = 0;
        _recheckQueue.Clear();
        _recheckMembership.Clear();
        lock (_scanGateLock)
        {
            _hasLastScanPosition = false;
            _lastScanPosition = Vector3.zero;
        }
    }

    // =========================
    // Loot detection
    // =========================

    private static bool IsLootableAndUnopened(World world, Vector3i pos, out string verbose, out BlockValue blockValue, out object tileEntity)
    {
        verbose = null;
        blockValue = default;
        tileEntity = null;
        try
        {
            if (!IsBlockInLoadedChunk(world, pos))
                return false;

            blockValue = world.GetBlock(pos);
            if (blockValue.type == 0)
                return false;

            object te = GetTileEntitySafe(world, pos);
            if (te != null)
            {
                tileEntity = te;
                string tn = te.GetType().Name.ToLowerInvariant();
                if (LooksLikeDoorType(tn))
                {
                    verbose = BuildVerboseEntry(pos, te.GetType().Name, null, -1, true) + " (door skipped)";
                    return false;
                }

                bool looksLikeContainer = tn.Contains("loot") || tn.Contains("container") || tn.Contains("secure");
                if (!looksLikeContainer) return false;

                bool? opened = TryGetOpenedTouched(te);
                int lootLevel = TryGetLootListIndex(te);
                verbose = BuildVerboseEntry(pos, te.GetType().Name, opened, lootLevel, true);

                if (opened.HasValue)
                    return opened.Value == false;

                // Unknown status => skip highlighting to avoid false positives
                return false;
            }

            var block = blockValue.Block;
            if (block == null) return false;

            bool looks = BlockLooksLootable(block);

            if (looks)
            {
                bool? assumedOpened = TryInferOpenedFromBlock(blockValue);
                verbose = BuildVerboseEntry(pos, block.GetType().Name, assumedOpened, ExtractLootIndex(block), false);

                if (assumedOpened.HasValue)
                    return assumedOpened.Value == false;

                // Unknown block state — skip to avoid false positives
                return false;
            }
            else if (LooksLikeDoorType(block.GetType().Name.ToLowerInvariant()))
            {
                verbose = BuildVerboseEntry(pos, block.GetType().Name, null, -1, false) + " (door skipped)";
                return false;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildVerboseEntry(Vector3i pos, string name, bool? opened, int lootLevel, bool tileEntity)
    {
        if (!VerboseLogging) return null;

        string openedTxt = opened.HasValue ? (opened.Value ? "opened" : "unopened") : "unknown";
        string lootTxt = lootLevel >= 0 ? lootLevel.ToString() : "n/a";
        var center = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
        string centerTxt = string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", center.x, center.y, center.z);
        return $"[PerceptionMasteryLootSense] {(tileEntity ? "TE" : "Block")} {name} at {pos} center={centerTxt} status={openedTxt} lootList={lootTxt}";
    }

    private static bool LooksLikeDoorType(string loweredTypeName)
    {
        if (string.IsNullOrEmpty(loweredTypeName))
            return false;

        return loweredTypeName.Contains("door")
               || loweredTypeName.Contains("hatch")
               || loweredTypeName.Contains("drawbridge")
               || loweredTypeName.Contains("garage")
               || loweredTypeName.Contains("vaultdoor")
               || loweredTypeName.Contains("portcullis");
    }

    private static bool IsBlockInLoadedChunk(World world, Vector3i pos)
    {
        if (world == null)
            return false;

        try
        {
            var worldType = world.GetType();

            _miWorldIsChunkLoadedXZ ??= worldType.GetMethod("IsChunkLoaded", new[] { typeof(int), typeof(int) });
            if (_miWorldIsChunkLoadedXZ != null)
            {
                int chunkX = pos.x >> ChunkCoordShift;
                int chunkZ = pos.z >> ChunkCoordShift;
                var result = _miWorldIsChunkLoadedXZ.Invoke(world, new object[] { chunkX, chunkZ });
                if (result is bool loaded)
                    return loaded;
            }

            _miWorldGetChunkFromWorldPos ??= worldType.GetMethod("GetChunkFromWorldPos", new[] { typeof(Vector3i) });
            if (_miWorldGetChunkFromWorldPos != null)
            {
                var chunk = _miWorldGetChunkFromWorldPos.Invoke(world, new object[] { pos });
                return chunk != null;
            }

            _miWorldGetChunkFromWorldPosXYZ ??= worldType.GetMethod("GetChunkFromWorldPos", new[] { typeof(int), typeof(int), typeof(int) });
            if (_miWorldGetChunkFromWorldPosXYZ != null)
            {
                var chunk = _miWorldGetChunkFromWorldPosXYZ.Invoke(world, new object[] { pos.x, pos.y, pos.z });
                return chunk != null;
            }
        }
        catch
        {
            // ignore lookup errors and fall back to assuming the chunk is loaded
        }

        return true;
    }

    private static bool? TryInferOpenedFromBlock(BlockValue blockValue)
    {
        try
        {
            var block = blockValue.Block;
            if (block == null)
                return null;

            var propsProperty = block.GetType().GetProperty("Properties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var propertiesInstance = propsProperty?.GetValue(block, null);
            if (propertiesInstance == null)
                return null;

            var valuesField = propertiesInstance.GetType().GetField("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var values = valuesField?.GetValue(propertiesInstance) as IDictionary;
            if (values == null)
                return null;

            if (values.Contains("lootOpened"))
            {
                var raw = values["lootOpened"];
                var val = raw?.ToString();
                if (bool.TryParse(val, out bool parsedBool))
                    return parsedBool;

                if (int.TryParse(val, out int parsedInt))
                    return parsedInt != 0;
            }

            if (blockValue.meta > 0 && blockValue.meta < 4 && block.GetBlockName()?.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
            // ignore inference errors
        }

        return null;
    }

    private readonly struct LootMarker
    {
        public LootMarker(Vector3i position, Mesh mesh, Vector3 pivotOffset, Vector3 localCenter, Quaternion rotation, float lastSeenTime)
        {
            Position = position;
            Mesh = mesh;
            PivotOffset = pivotOffset;
            LocalCenter = localCenter;
            Rotation = rotation;
            LastSeenTime = lastSeenTime;
        }

        public Vector3i Position { get; }
        public Mesh Mesh { get; }
        public Vector3 PivotOffset { get; }
        public Vector3 LocalCenter { get; }
        public Quaternion Rotation { get; }
        public float LastSeenTime { get; }
    }

    private sealed class Vector3iComparer : IEqualityComparer<Vector3i>
    {
        public bool Equals(Vector3i x, Vector3i y) => x.x == y.x && x.y == y.y && x.z == y.z;

        public int GetHashCode(Vector3i obj)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + obj.x;
                hash = hash * 31 + obj.y;
                hash = hash * 31 + obj.z;
                return hash;
            }
        }
    }

    private static bool BlockLooksLootable(Block block)
    {
        if (block == null)
            return false;

        var type = block.GetType();
        if (_blockLootableCache.TryGetValue(type, out bool cached))
            return cached;

        bool result = EvaluateBlockLootable(block);
        _blockLootableCache[type] = result;
        return result;
    }

    private static bool EvaluateBlockLootable(Block block)
    {
        try
        {
            string bn = (block.GetBlockName() ?? string.Empty).ToLowerInvariant();
            string cn = block.GetType().Name.ToLowerInvariant();

            if (bn.Contains("cnt") || bn.Contains("crate") || bn.Contains("chest") || bn.Contains("safe") || bn.Contains("cabinet") || bn.Contains("loot"))
                return true;
            if (cn.Contains("loot") || cn.Contains("container"))
                return true;

            var propsProp = block.GetType().GetProperty("Properties", MemberBindingFlags);
            var props = propsProp?.GetValue(block);
            if (props == null) return false;

            foreach (var key in new[] { "LootList", "LootListOnDestroy", "LootListOnRespawn", "LootContainer" })
            {
                var propsType = props.GetType();
                var miHasKey = propsType.GetMethod("ContainsKey", new[] { typeof(string) });
                if (miHasKey != null)
                {
                    var res = miHasKey.Invoke(props, new object[] { key });
                    if (res is bool b && b) return true;
                }

                var miContains = propsType.GetMethod("Contains", new[] { typeof(string) });
                if (miContains != null)
                {
                    var res = miContains.Invoke(props, new object[] { key });
                    if (res is bool b && b) return true;
                }
            }
        }
        catch
        {
            // ignore lookup failures and fall back to "not lootable"
        }

        return false;
    }

    private static int TryGetLootListIndex(object tileEntity)
    {
        try
        {
            var t = tileEntity.GetType();

            if (TryGetInt(t, tileEntity, out int lootIndex,
                    "lootListIndex", "LootListIndex", "lootIndex", "LootIndex"))
                return lootIndex;

            var prop = t.GetProperty("LootContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var container = prop?.GetValue(tileEntity);
            return ExtractLootIndex(container);
        }
        catch { }

        return -1;
    }

    private static int ExtractLootIndex(object obj)
    {
        if (obj == null) return -1;

        try
        {
            var t = obj.GetType();
            if (TryGetInt(t, obj, out int lootIndex,
                    "lootListIndex", "LootListIndex", "lootIndex", "LootIndex"))
                return lootIndex;

            var mi = t.GetMethod("GetLootListIndex") ?? t.GetMethod("GetLootList");
            if (mi != null)
            {
                var val = mi.Invoke(obj, Array.Empty<object>());
                if (val is int i)
                    return i;
            }
        }
        catch { }

        return -1;
    }

    private static bool? TryGetOpenedTouched(object tileEntity)
    {
        try
        {
            var t = tileEntity.GetType();

            if (TryGetBool(t, tileEntity, out bool playerStorage,
                "bPlayerStorage", "playerStorage", "PlayerStorage"))
            {
                if (playerStorage) return true;
            }

            if (TryGetBool(t, tileEntity, out bool touched,
                "bTouched", "touched", "Touched",
                "bWasTouched", "wasTouched", "WasTouched",
                "bHasBeenTouched", "hasBeenTouched", "HasBeenTouched",
                "bOpened", "opened", "Opened",
                "bWasOpened", "wasOpened", "WasOpened"))
            {
                return touched;
            }

            if (TryGetLong(t, tileEntity, out long wtt, "worldTimeTouched", "WorldTimeTouched"))
                return wtt > 0;

            if (TryGetInt(t, tileEntity, out int wtti, "worldTimeTouched", "WorldTimeTouched"))
                return wtti > 0;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBool(Type t, object obj, out bool value, params string[] names)
    {
        foreach (var n in names)
        {
            var f = GetCachedField(t, n);
            if (f != null && f.FieldType == typeof(bool))
            {
                value = (bool)f.GetValue(obj);
                return true;
            }

            var p = GetCachedProperty(t, n);
            if (p != null && p.PropertyType == typeof(bool) && p.GetIndexParameters().Length == 0)
            {
                value = (bool)p.GetValue(obj, null);
                return true;
            }
        }
        value = false;
        return false;
    }

    private static bool TryGetLong(Type t, object obj, out long value, params string[] names)
    {
        foreach (var n in names)
        {
            var f = GetCachedField(t, n);
            if (f != null && f.FieldType == typeof(long))
            {
                value = (long)f.GetValue(obj);
                return true;
            }

            var p = GetCachedProperty(t, n);
            if (p != null && p.PropertyType == typeof(long) && p.GetIndexParameters().Length == 0)
            {
                value = (long)p.GetValue(obj, null);
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static bool TryGetInt(Type t, object obj, out int value, params string[] names)
    {
        foreach (var n in names)
        {
            var f = GetCachedField(t, n);
            if (f != null && f.FieldType == typeof(int))
            {
                value = (int)f.GetValue(obj);
                return true;
            }

            var p = GetCachedProperty(t, n);
            if (p != null && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0)
            {
                value = (int)p.GetValue(obj, null);
                return true;
            }
        }
        value = 0;
        return false;
    }

    private static FieldInfo GetCachedField(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
            return null;

        string key = string.Concat(type.FullName, "::", name);
        lock (_memberCacheLock)
        {
            if (_fieldInfoCache.TryGetValue(key, out var cached))
                return cached;

            var field = type.GetField(name, MemberBindingFlags);
            _fieldInfoCache[key] = field;
            return field;
        }
    }

    private static PropertyInfo GetCachedProperty(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
            return null;

        string key = string.Concat(type.FullName, "::", name);
        lock (_memberCacheLock)
        {
            if (_propertyInfoCache.TryGetValue(key, out var cached))
                return cached;

            var prop = type.GetProperty(name, MemberBindingFlags);
            _propertyInfoCache[key] = prop;
            return prop;
        }
    }

    private static object GetTileEntitySafe(World world, Vector3i pos)
    {
        try
        {
            var t = world.GetType();

            var mi = t.GetMethod("GetTileEntity", new[] { typeof(int), typeof(Vector3i) });
            if (mi != null) return mi.Invoke(world, new object[] { 0, pos });

            mi = t.GetMethod("GetTileEntity", new[] { typeof(Vector3i) });
            if (mi != null) return mi.Invoke(world, new object[] { pos });
        }
        catch { }

        return null;
    }

    private static int GetPerkRank(EntityPlayerLocal player)
    {
        var prog = player?.Progression;
        if (prog == null) return 0;

        var pt = prog.GetType();

        try
        {
            _miGetPerkLevel ??= pt.GetMethod("GetPerkLevel", new[] { typeof(string) });
            if (_miGetPerkLevel != null)
                return (int)_miGetPerkLevel.Invoke(prog, new object[] { PerkName });

            _miGetProgressionValue ??= pt.GetMethod("GetProgressionValue", new[] { typeof(string) });
            if (_miGetProgressionValue != null)
            {
                var pv = _miGetProgressionValue.Invoke(prog, new object[] { PerkName });
                if (pv != null)
                {
                    var lprop = pv.GetType().GetProperty("Level") ?? pv.GetType().GetProperty("level");
                    if (lprop != null)
                        return Convert.ToInt32(lprop.GetValue(pv));
                }
            }
        }
        catch { }

        return 0;
    }
}
