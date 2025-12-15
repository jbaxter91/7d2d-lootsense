using System.Collections.Generic;
using System.Linq;
using UnityEngine;

internal sealed class MarkerRepository
{
    private readonly Dictionary<Vector3i, LootMarker> _activeMarkers = new(new Vector3iComparer());
    private readonly Queue<Vector3i> _recheckQueue = new();
    private readonly HashSet<Vector3i> _recheckMembership = new(new Vector3iComparer());
    private readonly object _lock = new();

    private readonly float _markerTimeoutSeconds;
    private readonly float _rangeGraceMeters;
    private readonly int _activeRechecksPerScan;

    public MarkerRepository(float markerTimeoutSeconds, float rangeGraceMeters, int activeRechecksPerScan)
    {
        _markerTimeoutSeconds = markerTimeoutSeconds;
        _rangeGraceMeters = rangeGraceMeters;
        _activeRechecksPerScan = activeRechecksPerScan;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _activeMarkers.Count;
        }
    }

    public LootMarker[] Snapshot()
    {
        lock (_lock)
            return _activeMarkers.Values.ToArray();
    }

    public void Clear()
    {
        lock (_lock)
            _activeMarkers.Clear();

        _recheckQueue.Clear();
        _recheckMembership.Clear();
    }

    public void ApplyUpdates(Dictionary<Vector3i, LootMarker> updates)
    {
        if (updates == null || updates.Count == 0)
            return;

        lock (_lock)
        {
            foreach (var kvp in updates)
                _activeMarkers[kvp.Key] = kvp.Value;
        }

        foreach (var key in updates.Keys)
            EnqueueForRecheck(key);

        updates.Clear();
    }

    public void Revalidate(World world, Vector3 playerPosition, float activeRadius, float now, LootScanner scanner)
    {
        if (world == null || scanner == null || _activeRechecksPerScan <= 0)
            return;

        int checks = Mathf.Min(_activeRechecksPerScan, _recheckQueue.Count);
        int processed = 0;
        float radiusLimit = Mathf.Max(0f, activeRadius) + _rangeGraceMeters;
        float radiusLimitSqr = radiusLimit * radiusLimit;

        while (_recheckQueue.Count > 0 && processed < checks)
        {
            var pos = _recheckQueue.Dequeue();
            _recheckMembership.Remove(pos);
            processed++;

            if (!IsWithinRadius(pos, playerPosition, radiusLimitSqr))
            {
                RemoveMarker(pos);
                continue;
            }

            if (!TryGetMarker(pos, out _))
                continue;

            if (!scanner.TryRefreshMarker(world, pos, now, out var refreshed))
            {
                RemoveMarker(pos);
                continue;
            }

            SetMarker(pos, refreshed);
            EnqueueForRecheck(pos);
        }
    }

    public void Prune(float now)
    {
        List<Vector3i> toRemove = null;

        lock (_lock)
        {
            foreach (var kvp in _activeMarkers)
            {
                if (now - kvp.Value.LastSeenTime <= _markerTimeoutSeconds)
                    continue;

                toRemove ??= new List<Vector3i>();
                toRemove.Add(kvp.Key);
            }

            if (toRemove != null)
            {
                foreach (var pos in toRemove)
                    _activeMarkers.Remove(pos);
            }
        }

        if (toRemove == null)
            return;

        foreach (var pos in toRemove)
            _recheckMembership.Remove(pos);
    }

    private void SetMarker(Vector3i pos, LootMarker marker)
    {
        lock (_lock)
            _activeMarkers[pos] = marker;
    }

    private bool TryGetMarker(Vector3i pos, out LootMarker marker)
    {
        lock (_lock)
            return _activeMarkers.TryGetValue(pos, out marker);
    }

    private void RemoveMarker(Vector3i pos)
    {
        lock (_lock)
            _activeMarkers.Remove(pos);

        _recheckMembership.Remove(pos);
    }

    private bool IsWithinRadius(Vector3i pos, Vector3 playerPosition, float radiusLimitSqr)
    {
        var markerCenter = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
        return (markerCenter - playerPosition).sqrMagnitude <= radiusLimitSqr;
    }

    private void EnqueueForRecheck(Vector3i pos)
    {
        if (_recheckMembership.Add(pos))
            _recheckQueue.Enqueue(pos);
    }
}
