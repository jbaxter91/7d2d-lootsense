using UnityEngine;

internal sealed class LootSenseController
{
    private const string PerkName = "perkPerceptionMastery";
    private static readonly float[] RankMeters = { 0f, 1f, 2f, 3f, 4f, 5f };

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

    private float _nextScanTime;
    private readonly object _scanGateLock = new();
    private Vector3 _lastScanPosition;
    private bool _hasLastScanPosition;

    private int _lastKnownRank;
    private float _lastComputedRadius;

    public LootSenseController()
    {
        _preferences = new LootSensePreferences();
        _markerRepository = new MarkerRepository(MarkerTimeoutSeconds, RangeGraceMeters, ActiveRechecksPerScan);
        _scanner = new LootScanner(VerboseLogging);
        _overlay = new LootOverlayManager(_preferences, _markerRepository, DebugMode, PositionTraceLogging, OverlayTraceIntervalSeconds);
        _overlay.NotifyPreferencesChanged();
    }

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

        _overlay.EnsureOverlay();

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

    public bool TrySetHighlightMode(string token, out HighlightMode mode, out string message)
    {
        bool success = _preferences.TrySetHighlightMode(token, out mode, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    public bool TrySetOpacity(string token, out string message)
    {
        bool success = _preferences.TrySetOpacity(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    public bool TrySetSize(string token, out string message)
    {
        bool success = _preferences.TrySetSize(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    public bool TrySetColor(string token, out string message)
    {
        bool success = _preferences.TrySetColor(token, out message);
        if (success)
            _overlay.NotifyPreferencesChanged();
        return success;
    }

    public bool TryAdjustRange(string token, out string message)
    {
        bool success = _preferences.TryAdjustRange(token, out message);
        if (success)
            message = string.Concat(message, $" (rank5 total {PreviewRadiusForRank(5):0.0}m)");
        return success;
    }

    public string GetHighlightModeSummary()
    {
        return _preferences.BuildStatusSummary(_lastComputedRadius);
    }

    public string GetConfigDump()
    {
        return _preferences.BuildConfigDump(PreviewRadiusForRank, _lastComputedRadius);
    }

    private float PreviewRadiusForRank(int rank)
    {
        float baseRadius = GetBaseRadiusForRank(rank);
        return Mathf.Max(0f, baseRadius + _preferences.RangeBonusMeters);
    }

    private static float GetBaseRadiusForRank(int rank)
    {
        int idx = Mathf.Clamp(rank, 0, RankMeters.Length - 1);
        return RankMeters[idx];
    }

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

    private void RecordScanPosition(Vector3 playerPosition)
    {
        lock (_scanGateLock)
        {
            _lastScanPosition = playerPosition;
            _hasLastScanPosition = true;
        }
    }

    private void ResetMovementGate()
    {
        lock (_scanGateLock)
        {
            _hasLastScanPosition = false;
            _lastScanPosition = Vector3.zero;
        }
    }
}
