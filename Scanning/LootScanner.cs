using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

internal sealed class LootScanner
{
    private const int MaxOffsetsPerScan = 1600;
    private const int EnumerableProbeLimit = 4;

    private static readonly string[] MeshMethodCandidates = { "GetPreviewMesh", "GetRenderMesh", "GetModelMesh", "GetMesh" };

    private readonly Dictionary<Vector3i, LootMarker> _scratchMarkers = new(new Vector3iComparer());
    private readonly HashSet<Vector3i> _loggedPositions = new(new Vector3iComparer());
    private readonly List<Vector3i> _scanOffsets = new();

    private readonly Dictionary<Type, bool> _blockLootableCache = new();
    private readonly Dictionary<string, FieldInfo> _fieldInfoCache = new();
    private readonly Dictionary<string, PropertyInfo> _propertyInfoCache = new();
    private readonly object _memberCacheLock = new();

    private readonly bool _verboseLogging;

    private int _scanOffsetsRadius;
    private int _scanOffsetCursor;

    private Mesh _fallbackCubeMesh;
    private Vector3 _fallbackCubePivot;
    private MethodInfo _miBlockRotation;
    private MethodInfo _miWorldIsChunkLoadedXZ;
    private MethodInfo _miWorldGetChunkFromWorldPos;
    private MethodInfo _miWorldGetChunkFromWorldPosXYZ;
    private MethodInfo _miGetPerkLevel;
    private MethodInfo _miGetProgressionValue;

    private readonly Dictionary<int, Mesh> _meshCache = new();
    private readonly Dictionary<int, Vector3> _meshPivotCache = new();
    private double _lastScanDurationMs;

    private const int ChunkCoordShift = 4;

    public LootScanner(bool verboseLogging)
    {
        _verboseLogging = verboseLogging;
    }

    public double LastScanDurationMs => _lastScanDurationMs;

    public void ScanAndMark(EntityPlayerLocal player, float radius, float now, MarkerRepository repository)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var world = player.world;
            if (world == null)
                return;

            Vector3 origin = player.GetPosition();
            var center = new Vector3i(
                Mathf.FloorToInt(origin.x),
                Mathf.FloorToInt(origin.y),
                Mathf.FloorToInt(origin.z)
            );

            int r = Mathf.CeilToInt(radius);
            if (r <= 0)
            {
                repository.Clear();
                return;
            }

            EnsureScanOffsets(r);
            int totalOffsets = _scanOffsets.Count;
            if (totalOffsets == 0)
                return;

            int batchSize = Mathf.Min(totalOffsets, MaxOffsetsPerScan);
            _scratchMarkers.Clear();

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
                _scratchMarkers[pos] = marker;

                if (_verboseLogging && !_loggedPositions.Contains(pos))
                {
                    if (!string.IsNullOrEmpty(verboseState))
                        Debug.Log(verboseState);
                    _loggedPositions.Add(pos);
                }
            }

            repository.ApplyUpdates(_scratchMarkers);
        }
        finally
        {
            stopwatch.Stop();
            _lastScanDurationMs = stopwatch.Elapsed.TotalMilliseconds;
        }
    }

    public bool TryRefreshMarker(World world, Vector3i position, float timestamp, out LootMarker marker)
    {
        marker = default;
        if (!IsLootableAndUnopened(world, position, out _, out BlockValue blockValue, out object visualSource))
            return false;

        marker = BuildLootMarker(position, blockValue, visualSource, timestamp);
        return true;
    }

    public int GetPerkRank(EntityPlayerLocal player, string perkName)
    {
        var progression = player?.Progression;
        if (progression == null)
            return 0;

        var progType = progression.GetType();

        try
        {
            _miGetPerkLevel ??= progType.GetMethod("GetPerkLevel", new[] { typeof(string) });
            if (_miGetPerkLevel != null)
                return (int)_miGetPerkLevel.Invoke(progression, new object[] { perkName });

            _miGetProgressionValue ??= progType.GetMethod("GetProgressionValue", new[] { typeof(string) });
            if (_miGetProgressionValue != null)
            {
                var pv = _miGetProgressionValue.Invoke(progression, new object[] { perkName });
                if (pv != null)
                {
                    var levelProp = pv.GetType().GetProperty("Level") ?? pv.GetType().GetProperty("level");
                    if (levelProp != null)
                        return Convert.ToInt32(levelProp.GetValue(pv));
                }
            }
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    private void EnsureScanOffsets(int radius)
    {
        if (radius == _scanOffsetsRadius && _scanOffsets.Count > 0)
            return;

        _scanOffsets.Clear();
        _scanOffsetsRadius = radius;
        _scanOffsetCursor = 0;

        if (radius <= 0)
            return;

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

    private LootMarker BuildLootMarker(Vector3i pos, BlockValue blockValue, object visualSource, float timestamp)
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

    private bool IsLootableAndUnopened(World world, Vector3i pos, out string verbose, out BlockValue blockValue, out object tileEntity)
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

    private string BuildVerboseEntry(Vector3i pos, string name, bool? opened, int lootLevel, bool tileEntity)
    {
        if (!_verboseLogging) return null;

        string openedTxt = opened.HasValue ? (opened.Value ? "opened" : "unopened") : "unknown";
        string lootTxt = lootLevel >= 0 ? lootLevel.ToString() : "n/a";
        var center = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
        string centerTxt = string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", center.x, center.y, center.z);
        return $"[PerceptionMasteryLootSense] {(tileEntity ? "TE" : "Block")} {name} at {pos} center={centerTxt} status={openedTxt} lootList={lootTxt}";
    }

    private bool LooksLikeDoorType(string loweredTypeName)
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

    private bool IsBlockInLoadedChunk(World world, Vector3i pos)
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
            // ignore lookup errors and fall back
        }

        return true;
    }

    private bool? TryInferOpenedFromBlock(BlockValue blockValue)
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

    private bool BlockLooksLootable(Block block)
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

    private bool EvaluateBlockLootable(Block block)
    {
        try
        {
            string bn = (block.GetBlockName() ?? string.Empty).ToLowerInvariant();
            string cn = block.GetType().Name.ToLowerInvariant();

            if (bn.Contains("cnt") || bn.Contains("crate") || bn.Contains("chest") || bn.Contains("safe") || bn.Contains("cabinet") || bn.Contains("loot"))
                return true;
            if (cn.Contains("loot") || cn.Contains("container"))
                return true;

            var propsProp = block.GetType().GetProperty("Properties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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
            // ignore lookup failures
        }

        return false;
    }

    private int TryGetLootListIndex(object tileEntity)
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
        catch
        {
            return -1;
        }
    }

    private int ExtractLootIndex(object obj)
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
        catch
        {
            // ignored
        }

        return -1;
    }

    private bool? TryGetOpenedTouched(object tileEntity)
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

    private bool TryGetBool(Type t, object obj, out bool value, params string[] names)
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

    private bool TryGetLong(Type t, object obj, out long value, params string[] names)
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

    private bool TryGetInt(Type t, object obj, out int value, params string[] names)
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

    private FieldInfo GetCachedField(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
            return null;

        string key = string.Concat(type.FullName, "::", name);
        lock (_memberCacheLock)
        {
            if (_fieldInfoCache.TryGetValue(key, out var cached))
                return cached;

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _fieldInfoCache[key] = field;
            return field;
        }
    }

    private PropertyInfo GetCachedProperty(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
            return null;

        string key = string.Concat(type.FullName, "::", name);
        lock (_memberCacheLock)
        {
            if (_propertyInfoCache.TryGetValue(key, out var cached))
                return cached;

            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _propertyInfoCache[key] = prop;
            return prop;
        }
    }

    private object GetTileEntitySafe(World world, Vector3i pos)
    {
        try
        {
            var t = world.GetType();

            var mi = t.GetMethod("GetTileEntity", new[] { typeof(int), typeof(Vector3i) });
            if (mi != null) return mi.Invoke(world, new object[] { 0, pos });

            mi = t.GetMethod("GetTileEntity", new[] { typeof(Vector3i) });
            if (mi != null) return mi.Invoke(world, new object[] { pos });
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private Mesh EnsureFallbackCubeMesh()
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

    private bool TryResolveMesh(BlockValue blockValue, object visualSource, out Mesh mesh, out Vector3 pivot)
    {
        int cacheKey = blockValue.GetHashCode();
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

    private Mesh BuildMesh(BlockValue blockValue)
    {
        var block = blockValue.Block;
        if (block == null) return null;

        if (TryExtractMesh(block.shape, block, blockValue, out var mesh))
            return mesh;

        if (TryExtractMesh(block, block, blockValue, out mesh))
            return mesh;

        return null;
    }

    private bool TryExtractMesh(object provider, Block block, BlockValue blockValue, out Mesh mesh)
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
                // Ignore failures
            }
        }

        return false;
    }

    private bool TryExtractMeshFromMembers(object provider, out Mesh mesh)
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

    private bool TryConvertObjectToMesh(string memberName, object candidate, out Mesh mesh)
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

    private bool IsRenderableMesh(Mesh mesh)
    {
        return mesh != null && mesh.vertexCount > 0 && mesh.triangles != null && mesh.triangles.Length > 0;
    }

    private bool TryBuildMeshFromDescription(object desc, string memberName, out Mesh mesh)
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

    private object[] BuildMeshArgs(ParameterInfo[] parameters, Block block, BlockValue blockValue)
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

    private Quaternion ResolveRotation(BlockValue blockValue)
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
            // ignored
        }

        return Quaternion.identity;
    }

    private MethodInfo FindRotationMethod()
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
}
