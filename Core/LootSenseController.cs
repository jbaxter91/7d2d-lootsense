using System.Globalization;
using UnityEngine;

/// <summary>
/// Coordinates scanning, marker lifecycle, overlay rendering, and profiler feedback for LootSense.
/// </summary>
internal sealed class LootSenseController
{
    private const string PerkName = "perkPerceptionMastery";
    private static readonly float[] RankMeters = { 0f, 2f, 4f, 6f, 8f, 10f };

    private const float ScanIntervalSeconds = 0.30f;
    private const float MovementScanThresholdMeters = 0.35f;
    private const float MarkerTimeoutSeconds = 10f;
    private const float RangeGraceMeters = 1.5f;
    private const int ActiveRechecksPerScan = 256;

    private const bool DebugMode = false;
    private const bool VerboseLogging = false;
    private const bool PositionTraceLogging = false;
    private const float OverlayTraceIntervalSeconds = 2f;

    private readonly LootSensePreferences _preferences;
    private readonly MarkerRepository _markerRepository;
    private readonly LootScanner _scanner;
    private readonly LootOverlayManager _overlay;
    private readonly LootSenseProfilerDisplay _profilerDisplay;

    private float _nextScanTime;
    private readonly object _scanGateLock = new();
    private Vector3 _lastScanPosition;
    private bool _hasLastScanPosition;

    private int _lastKnownRank;
    private float _lastComputedRadius;

    private bool _systemEnabled = true;
    private bool _scanningEnabled = true;
    private bool _renderingEnabled = true;
    private bool _profilerEnabled;

    /// <summary>
    /// Builds the shared preferences, repositories, scanner, overlay, and profiler display instances.
    /// </summary>
    public LootSenseController()
    {
        _preferences = new LootSensePreferences();
        _markerRepository = new MarkerRepository(MarkerTimeoutSeconds, RangeGraceMeters, ActiveRechecksPerScan);
        _scanner = new LootScanner(VerboseLogging);
        _overlay = new LootOverlayManager(_preferences, _markerRepository, DebugMode, PositionTraceLogging, OverlayTraceIntervalSeconds);
        _overlay.NotifyPreferencesChanged();
        _profilerDisplay = new LootSenseProfilerDisplay(BuildProfilerReadout);
    }

    /// <summary>
    /// Main update hook invoked every frame for the local player to drive scans and overlay refreshes.
    /// </summary>
    public void OnPlayerUpdate(EntityPlayerLocal player)
    {
        if (player == null || player.IsDead())
        {
            _markerRepository.Clear();
            ResetMovementGate();
            _lastKnownRank = 0;
            _lastComputedRadius = 0f;
            return;
        }

        if (!_systemEnabled)
        {
            _markerRepository.Clear();
            return;
        }

        if (_renderingEnabled)
            _overlay.EnsureOverlay();

        if (!_scanningEnabled)
            return;

        float now = Time.time;
        if (now < _nextScanTime)
            return;

        _nextScanTime = now + ScanIntervalSeconds;

        _lastKnownRank = _scanner.GetPerkRank(player, PerkName);
        if (_lastKnownRank <= 0)
        {
            _markerRepository.Clear();
            ResetMovementGate();
            _lastComputedRadius = 0f;
            return;
        }

        float radius = PreviewRadiusForRank(_lastKnownRank);
        _lastComputedRadius = radius;

        if (radius <= 0.01f)
        {
            _markerRepository.Clear();
            return;
        }

        Vector3 playerPos = player.GetPosition();
        bool shouldScan = ShouldPerformFullScan(playerPos);

        if (shouldScan)
        {
            RecordScanPosition(playerPos);
            _scanner.ScanAndMark(player, radius, now, _markerRepository);
        }

        var world = player.world;
        _markerRepository.Revalidate(world, playerPos, radius, now, _scanner);
        _markerRepository.Prune(now);
    }

    /// <summary>
    /// Reports that highlight mode is locked to icons for compatibility with existing commands.
    /// </summary>
    public bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
        => _preferences.TrySetHighlightMode(token, out mode, out message);

    /// <summary>
    /// Updates overlay opacity and refreshes materials if the value parses successfully.
    /// </summary>
    public bool TrySetOpacity(string token, out string message)
    {
        bool success = _preferences.TrySetOpacity(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    /// <summary>
    /// Changes box/icon scaling preferences and notifies the overlay when the value is valid.
    /// </summary>
    public bool TrySetSize(string token, out string message)
    {
        bool success = _preferences.TrySetSize(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    /// <summary>
    /// Applies a new highlight color parsed from hex text and propagates the change to materials.
    /// </summary>
    public bool TrySetColor(string token, out string message)
    {
        bool success = _preferences.TrySetColor(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    /// <summary>
    /// Nudges the perk range bonus and reports the new effective maximum to the user.
    /// </summary>
    public bool TryAdjustRange(string token, out string message)
    {
        bool success = _preferences.TryAdjustRange(token, out message);
        if (success)
            message = string.Concat(message, $" (rank5 total {PreviewRadiusForRank(5):0.0}m)");
        return success;
    }

    /// <summary>
    /// Toggles the entire LootSense system on/off, clearing markers and overlay state when needed.
    /// </summary>
    public bool TrySetSystemState(string token, out string message)
    {
        if (!TryParseToggle(token, out bool enabled, out message))
            return false;

        if (_systemEnabled == enabled)
        {
            message = $"System already {(enabled ? "enabled" : "disabled")}.";
            return true;
        }

        _systemEnabled = enabled;
        if (!enabled)
        {
            _markerRepository.Clear();
            ResetMovementGate();
            _nextScanTime = Time.time;
        }
        else
        {
            ResetMovementGate();
            _nextScanTime = Time.time;
        }

        SyncOverlayState();
        message = enabled ? "LootSense system enabled." : "LootSense system disabled.";
        return true;
    }

    /// <summary>
    /// Enables or disables scanning without altering rendering so players can pause work temporarily.
    /// </summary>
    public bool TrySetScanningState(string token, out string message)
    {
        if (!TryParseToggle(token, out bool enabled, out message))
            return false;

        if (_scanningEnabled == enabled)
        {
            message = $"Scanning already {(enabled ? "enabled" : "disabled")}.";
            return true;
        }

        _scanningEnabled = enabled;
        ResetMovementGate();
        if (enabled)
            _nextScanTime = Time.time;

        message = enabled ? "Scanning enabled." : "Scanning disabled.";
        return true;
    }

    /// <summary>
    /// Controls whether overlay drawing should occur while keeping scan data intact.
    /// </summary>
    public bool TrySetRenderingState(string token, out string message)
    {
        if (!TryParseToggle(token, out bool enabled, out message))
            return false;

        if (_renderingEnabled == enabled)
        {
            message = $"Rendering already {(enabled ? "enabled" : "disabled")}.";
            return true;
        }

        _renderingEnabled = enabled;
        if (enabled)
        {
            ResetMovementGate();
            _nextScanTime = Time.time;
        }

        SyncOverlayState();
        message = enabled ? "Rendering enabled." : "Rendering disabled.";
        return true;
    }

    /// <summary>
    /// Shows or hides the HUD profiler widget that surfaces timing metrics.
    /// </summary>
    public bool TrySetProfilerState(string token, out string message)
    {
        if (!TryParseToggle(token, out bool enabled, out message))
            return false;

        if (_profilerEnabled == enabled)
        {
            message = $"Profiler already {(enabled ? "enabled" : "disabled")}.";
            return true;
        }

        _profilerEnabled = enabled;
        _profilerDisplay.SetEnabled(enabled);
        message = enabled ? "LootSense profiler display enabled." : "LootSense profiler display disabled.";
        return true;
    }

    /// <summary>
    /// Builds the short-form status string the console displays when users request pm_lootsense status.
    /// </summary>
    public string GetHighlightModeSummary()
    {
        var summary = _preferences.BuildStatusSummary(_lastComputedRadius);
        return string.Concat(summary,
            " system=", FormatToggle(_systemEnabled),
            " scanning=", FormatToggle(_scanningEnabled),
            " rendering=", FormatToggle(_renderingEnabled),
            " profiler=", FormatToggle(_profilerEnabled));
    }

    /// <summary>
    /// Produces a verbose diagnostic snapshot of all user preferences and derived rank ranges.
    /// </summary>
    public string GetConfigDump()
    {
        var baseDump = _preferences.BuildConfigDump(PreviewRadiusForRank, _lastComputedRadius).TrimEnd();
        return string.Concat(baseDump,
            "\n  systemEnabled=", _systemEnabled ? "true" : "false",
            "\n  scanningEnabled=", _scanningEnabled ? "true" : "false",
            "\n  renderingEnabled=", _renderingEnabled ? "true" : "false",
            "\n  profilerEnabled=", _profilerEnabled ? "true" : "false",
            "\n");
    }

    /// <summary>
    /// Combines the perk base radius with user bonuses for a quick preview of a given rank.
    /// </summary>
    private float PreviewRadiusForRank(int rank)
    {
        float baseRadius = GetBaseRadiusForRank(rank);
        return Mathf.Max(0f, baseRadius + _preferences.RangeBonusMeters);
    }

    /// <summary>
    /// Returns the baked-in vanilla radius per perk rank while clamping out-of-range input.
    /// </summary>
    private static float GetBaseRadiusForRank(int rank)
    {
        int idx = Mathf.Clamp(rank, 0, RankMeters.Length - 1);
        return RankMeters[idx];
    }

    /// <summary>
    /// Movement gating ensures full scans only run when the player has moved enough to matter.
    /// </summary>
    private bool ShouldPerformFullScan(Vector3 playerPosition)
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

    /// <summary>
    /// Stores the most recent position that triggered a scan so future movement checks have state.
    /// </summary>
    private void RecordScanPosition(Vector3 playerPosition)
    {
        lock (_scanGateLock)
        {
            _lastScanPosition = playerPosition;
            _hasLastScanPosition = true;
        }
    }

    /// <summary>
    /// Clears cached movement state so the next scan request is allowed immediately.
    /// </summary>
    private void ResetMovementGate()
    {
        lock (_scanGateLock)
        {
            _hasLastScanPosition = false;
            _lastScanPosition = Vector3.zero;
        }
    }

    /// <summary>
    /// Overlay rendering mirrors the high-level system/render toggles so the HUD never lingers unexpectedly.
    /// </summary>
    private void SyncOverlayState()
    {
        bool shouldRender = _systemEnabled && _renderingEnabled;
        _overlay.SetRenderingEnabled(shouldRender);
    }

    /// <summary>
    /// Aggregates the latest scan/render timings and builds the HUD-friendly profiler string.
    /// </summary>
    private string BuildProfilerReadout()
    {
        double scanMs = _scanner.LastScanDurationMs;
        double renderMs = _overlay.LastRenderDurationMs;
        int markerCount = _markerRepository.Count;
        float dt = Time.smoothDeltaTime > 0f ? Time.smoothDeltaTime : Time.deltaTime;
        float fps = dt > 0f ? 1f / dt : 0f;

        return string.Format(CultureInfo.InvariantCulture,
            "LootSense Profiler\nScanning: {0:0.00} ms\nRendering: {1:0.00} ms\nMarkers: {2}\nFPS: {3:0.0}",
            scanMs,
            renderMs,
            markerCount,
            fps);
    }

    /// <summary>
    /// Converts on/off style tokens into boolean values shared across every toggle command.
    /// </summary>
    private static bool TryParseToggle(string token, out bool enabled, out string message)
    {
        enabled = true;
        message = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            message = "Missing state. Use on/off.";
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "on":
            case "true":
            case "1":
            case "enable":
            case "enabled":
                enabled = true;
                return true;
            case "off":
            case "false":
            case "0":
            case "disable":
            case "disabled":
                enabled = false;
                return true;
            default:
                message = "Invalid state. Use on/off.";
                return false;
        }
    }

    /// <summary>
    /// Normalizes boolean states into on/off text for status printouts.
    /// </summary>
    private static string FormatToggle(bool value) => value ? "on" : "off";
}
