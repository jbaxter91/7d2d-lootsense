using System;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class LootOverlayManager
{
    private const float IconVerticalOffset = 0f;
    private const string IconSpriteName = "perkPerceptionMastery";

    private static readonly Vector2[] DefaultIconUVs =
    {
        new(0f, 0f),
        new(1f, 0f),
        new(0f, 1f),
        new(1f, 1f)
    };

    private readonly LootSensePreferences _preferences;
    private readonly MarkerRepository _markerRepository;
    private readonly bool _debugMode;
    private readonly bool _positionTraceLogging;
    private readonly float _overlayTraceIntervalSeconds;

    private LootSenseOverlayBehaviour _overlayBehaviour;

    private Material _fillMaterial;
    private Material _outlineMaterial;
    private Material _iconMaterial;
    private Mesh _iconQuadMesh;
    private Texture2D _iconTexture;
    private Sprite _iconSprite;
    private Texture2D _generatedIconTexture;
    private bool _loggedMissingIcon;

    private Mesh _fallbackCubeMesh;
    private Vector3 _fallbackCubePivot;

    private Vector3 _worldOriginOffset = Vector3.zero;
    private float _nextOverlayTraceTime;
    private float _nextDebugPingTime;
    private bool _renderingEnabled = true;
    private double _lastRenderDurationMs;
    private int _lastSampleFrame = -1;
    private bool _hasSampleThisFrame;

    public double LastRenderDurationMs => _lastRenderDurationMs;

    public LootOverlayManager(LootSensePreferences preferences, MarkerRepository markerRepository, bool debugMode, bool positionTraceLogging, float overlayTraceIntervalSeconds)
    {
        _preferences = preferences;
        _markerRepository = markerRepository;
        _debugMode = debugMode;
        _positionTraceLogging = positionTraceLogging;
        _overlayTraceIntervalSeconds = overlayTraceIntervalSeconds;
    }

    public void EnsureOverlay()
    {
        if (!_renderingEnabled)
            return;

        if (_overlayBehaviour != null)
        {
            if (!_overlayBehaviour.enabled)
                _overlayBehaviour.enabled = true;
            return;
        }

        var go = new GameObject("PerceptionMasteryLootSenseOverlay");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _overlayBehaviour = go.AddComponent<LootSenseOverlayBehaviour>();
        _overlayBehaviour.Initialize(this);

        if (_debugMode)
            Debug.Log("[PerceptionMasteryLootSense] Overlay renderer created.");
    }

    public void SetRenderingEnabled(bool enabled)
    {
        _renderingEnabled = enabled;

        if (!_renderingEnabled)
        {
            if (_overlayBehaviour != null)
                _overlayBehaviour.enabled = false;
            return;
        }

        EnsureOverlay();
    }

    public void NotifyPreferencesChanged()
    {
        ApplyVisualSettings();
        ApplyIconUVs();
    }

    private bool ShouldSkipCamera(Camera cam)
    {
        if (cam == null)
            return true;

        if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.VR)
            return true;

        var lowered = (cam.name ?? string.Empty).ToLowerInvariant();
        if (lowered.Contains("ui") || lowered.Contains("preview"))
            return true;

        return false;
    }

    private void ApplyVisualSettings()
    {
        var alpha = _preferences.Alpha;
        var fillColor = new Color(_preferences.UserColor.r, _preferences.UserColor.g, _preferences.UserColor.b, alpha);
        var outlineColor = new Color(_preferences.UserColor.r, _preferences.UserColor.g, _preferences.UserColor.b, Mathf.Clamp01(alpha * 1.35f));

        if (_fillMaterial != null)
        {
            _fillMaterial.SetColor("_Color", fillColor);
            _fillMaterial.EnableKeyword("_EMISSION");
            _fillMaterial.SetColor("_EmissionColor", fillColor * 2.2f);
        }

        if (_outlineMaterial != null)
            _outlineMaterial.SetColor("_Color", outlineColor);

        if (_iconMaterial != null)
            _iconMaterial.color = fillColor;
    }

    internal void DrawForCamera(Camera cam)
    {
        int currentFrame = Time.frameCount;
        if (currentFrame != _lastSampleFrame)
        {
            _lastSampleFrame = currentFrame;
            _hasSampleThisFrame = false;
        }

        if (!_renderingEnabled)
        {
            if (!_hasSampleThisFrame)
            {
                _lastRenderDurationMs = 0;
                _hasSampleThisFrame = true;
            }
            return;
        }

        if (ShouldSkipCamera(cam))
        {
            if (!_hasSampleThisFrame)
            {
                _lastRenderDurationMs = 0;
                _hasSampleThisFrame = true;
            }
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            bool iconMode = _preferences.HighlightMode == HighlightMode.Icon;

            if (iconMode)
            {
                if (!EnsureIconResources())
                    return;
            }
            else
            {
                EnsureBoxResources();
                if (_fillMaterial == null || _outlineMaterial == null)
                    return;
            }

            var snapshot = _markerRepository.Snapshot();
            if (snapshot.Length == 0)
                return;

            RefreshWorldOriginOffset();

            if (_debugMode && Time.time >= _nextDebugPingTime)
            {
                _nextDebugPingTime = Time.time + 5f;
                Debug.Log($"[PerceptionMasteryLootSense] Draw callback OK on camera '{cam.name}'");
            }

            if (_debugMode && _positionTraceLogging && Time.time >= _nextOverlayTraceTime)
            {
                _nextOverlayTraceTime = Time.time + _overlayTraceIntervalSeconds;
                LogOverlayTrace(cam, snapshot);
            }

            if (iconMode)
                GL.Clear(true, false, Color.black);

            foreach (var marker in snapshot)
            {
                if (iconMode)
                    DrawIconForMarker(marker, cam);
                else
                    DrawBoxForMarker(marker);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PerceptionMasteryLootSense] Overlay draw error: {e.Message}");
        }
        finally
        {
            stopwatch.Stop();
            _lastRenderDurationMs = stopwatch.Elapsed.TotalMilliseconds;
            _hasSampleThisFrame = true;
        }
    }

    private void DrawIconForMarker(LootMarker marker, Camera cam)
    {
        if (_iconMaterial == null || _iconQuadMesh == null)
            return;

        float scale = _preferences.IconScale;
        if (scale <= 0f)
            return;

        var worldCenter = new Vector3(marker.Position.x + 0.5f, marker.Position.y + 0.5f, marker.Position.z + 0.5f);
        var renderCenter = worldCenter - _worldOriginOffset + new Vector3(0f, IconVerticalOffset, 0f);

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

    private void DrawBoxForMarker(LootMarker marker)
    {
        var cube = EnsureFallbackCubeMesh();
        if (cube == null || _fillMaterial == null || _outlineMaterial == null)
            return;

        float fillScale = Mathf.Max(0f, _preferences.BoxScale);
        if (fillScale <= 0f)
            return;

        var worldCenter = new Vector3(marker.Position.x + 0.5f, marker.Position.y + 0.5f, marker.Position.z + 0.5f);
        var renderCenter = worldCenter - _worldOriginOffset;

        var fillMatrix = Matrix4x4.TRS(renderCenter, Quaternion.identity, Vector3.one * fillScale);
        if (_fillMaterial.SetPass(0))
            Graphics.DrawMeshNow(cube, fillMatrix);

        float outlineScale = Mathf.Max(fillScale, _preferences.OutlineScale);
        var outlineMatrix = Matrix4x4.TRS(renderCenter, Quaternion.identity, Vector3.one * outlineScale);
        if (_outlineMaterial.SetPass(0))
            Graphics.DrawMeshNow(cube, outlineMatrix);
    }

    private void LogOverlayTrace(Camera cam, LootMarker[] snapshot)
    {
        var sb = new StringBuilder();
        var camPos = cam.transform.position;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "[PerceptionMasteryLootSense] Overlay trace cam='{0}' camPos=({1:F2}, {2:F2}, {3:F2}) targets={4}",
            cam.name, camPos.x, camPos.y, camPos.z, snapshot.Length);

        int inspect = Mathf.Min(snapshot.Length, 4);
        for (int i = 0; i < inspect; i++)
        {
            var marker = snapshot[i];
            var pos = marker.Position;
            var worldCenter = new Vector3(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);
            var renderCenter = worldCenter - _worldOriginOffset;
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

    private void RefreshWorldOriginOffset()
    {
        try
        {
            var gm = GameManager.Instance;
            var world = gm?.World;
            var player = world?.GetPrimaryPlayer() as EntityPlayerLocal;
            if (player == null)
                return;

            var playerTransform = player.transform;
            if (playerTransform == null)
                return;

            Vector3 worldPos = player.GetPosition();
            Vector3 localPos = playerTransform.position;
            _worldOriginOffset = worldPos - localPos;
        }
        catch (Exception e)
        {
            if (_debugMode && _positionTraceLogging)
                Debug.LogWarning($"[PerceptionMasteryLootSense] Failed to refresh world origin offset: {e.Message}");
        }
    }

    private void EnsureBoxResources()
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

    private void ConfigureHighlightMaterial(Material mat)
    {
        if (mat == null)
            return;

        mat.SetInt("_ZWrite", 0);
        mat.SetInt("_ZTest", (int)CompareFunction.Always);
        mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_Cull", (int)CullMode.Off);
    }

    private bool EnsureIconResources()
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

    private Mesh BuildIconQuadMesh()
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

    private Sprite LocatePerceptionIconSprite()
    {
        try
        {
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            return sprites.FirstOrDefault(sprite => sprite != null && sprite.name.Equals(IconSpriteName, StringComparison.OrdinalIgnoreCase));
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

    private Texture2D LocatePerceptionIconTextureFallback()
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
            // ignore
        }

        return EnsureGeneratedIconTexture();
    }

    private Texture2D EnsureGeneratedIconTexture()
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

        Color[] pixels = new Color[size * size];
        Color transparent = new Color(0f, 0f, 0f, 0f);
        float half = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x - half) / half;
                float ny = (y - half) / half;

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

    private void ApplyIconUVs()
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

    private Mesh EnsureFallbackCubeMesh()
    {
        if (_fallbackCubeMesh != null)
            return _fallbackCubeMesh;

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = cube.GetComponent<MeshFilter>();
        if (meshFilter != null)
        {
            _fallbackCubeMesh = UnityEngine.Object.Instantiate(meshFilter.sharedMesh);
            _fallbackCubeMesh.name = "PMLootSenseOverlayCube";
            _fallbackCubePivot = -_fallbackCubeMesh.bounds.min;
        }

        UnityEngine.Object.Destroy(cube);
        return _fallbackCubeMesh;
    }

    private sealed class LootSenseOverlayBehaviour : MonoBehaviour
    {
        private LootOverlayManager _manager;
        private bool _srpActive;

        public void Initialize(LootOverlayManager manager)
        {
            _manager = manager;
        }

        private void OnEnable()
        {
            _srpActive = GraphicsSettings.renderPipelineAsset != null;
            Camera.onPostRender += OnPostRenderBuiltin;
            RenderPipelineManager.endCameraRendering += OnEndCameraRenderingSRP;
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
            _manager?.DrawForCamera(cam);
        }

        private void OnEndCameraRenderingSRP(ScriptableRenderContext ctx, Camera cam)
        {
            _manager?.DrawForCamera(cam);
        }
    }
}
