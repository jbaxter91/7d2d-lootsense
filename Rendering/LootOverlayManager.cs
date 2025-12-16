using System;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Owns the highlight overlay lifecycle, including resource setup and per-camera drawing.
/// </summary>
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

    private Material _iconMaterial;
    private Mesh _iconQuadMesh;
    private Texture2D _iconTexture;
    private Sprite _iconSprite;
    private Texture2D _generatedIconTexture;
    private bool _loggedMissingIcon;

    private Vector3 _worldOriginOffset = Vector3.zero;
    private float _nextOverlayTraceTime;
    private float _nextDebugPingTime;
    private bool _renderingEnabled = true;
    private double _lastRenderDurationMs;
    private int _lastSampleFrame = -1;
    private bool _hasSampleThisFrame;

    /// <summary>
    /// Duration in milliseconds of the most recent overlay render pass.
    /// </summary>
    public double LastRenderDurationMs => _lastRenderDurationMs;

    /// <summary>
    /// Creates the overlay manager with shared dependencies and runtime tracing preferences.
    /// </summary>
    public LootOverlayManager(LootSensePreferences preferences, MarkerRepository markerRepository, bool debugMode, bool positionTraceLogging, float overlayTraceIntervalSeconds)
    {
        _preferences = preferences;
        _markerRepository = markerRepository;
        _debugMode = debugMode;
        _positionTraceLogging = positionTraceLogging;
        _overlayTraceIntervalSeconds = overlayTraceIntervalSeconds;
    }

    /// <summary>
    /// Creates the persistent overlay GameObject/behaviour the first time rendering is requested.
    /// </summary>
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

    /// <summary>
    /// External toggles can suspend rendering without tearing down all cached resources.
    /// </summary>
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

    /// <summary>
    /// Reapplies material colors/UVs whenever preferences mutate.
    /// </summary>
    public void NotifyPreferencesChanged()
    {
        ApplyVisualSettings();
        ApplyIconUVs();
    }

    /// <summary>
    /// Skip UI/preview cameras so the overlay only draws on gameplay views.
    /// </summary>
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

    /// <summary>
    /// Pushes the latest colors/alpha values into cached materials.
    /// </summary>
    private void ApplyVisualSettings()
    {
        var alpha = _preferences.Alpha;
        var iconColor = new Color(_preferences.UserColor.r, _preferences.UserColor.g, _preferences.UserColor.b, alpha);

        if (_iconMaterial != null)
            _iconMaterial.color = iconColor;
    }

    /// <summary>
    /// Main entry invoked for each eligible camera; handles resource checks, sampling, and timing.
    /// </summary>
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
            if (!EnsureIconResources())
                return;

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

            GL.Clear(true, false, Color.black);

            foreach (var marker in snapshot)
                DrawIconForMarker(marker, cam);
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

    /// <summary>
    /// Renders a billboarded icon for the given marker when icon mode is active.
    /// </summary>
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

    /// <summary>
    /// Periodically logs camera/marker positional data when debug tracing is enabled.
    /// </summary>
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

    /// <summary>
    /// Keeps gizmos aligned when Unity shifts the floating origin in large worlds.
    /// </summary>
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

    /// <summary>
    /// Resolves the icon mesh, sprite, and materials used for icon highlight mode.
    /// </summary>
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

    /// <summary>
    /// Creates the billboard quad mesh used for icon rendering.
    /// </summary>
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

    /// <summary>
    /// Attempts to find the perception mastery sprite from loaded resources.
    /// </summary>
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

    /// <summary>
    /// Looks for a loose texture fallback when the sprite asset cannot be found.
    /// </summary>
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

    /// <summary>
    /// Generates a tiny eye texture fallback when the perception sprite is not present.
    /// </summary>
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

    /// <summary>
    /// Updates the quad UVs to match the selected sprite or resets to defaults.
    /// </summary>
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

    // Lightweight behaviour that bridges Unity's render callbacks back into this manager.
    private sealed class LootSenseOverlayBehaviour : MonoBehaviour
    {
        private LootOverlayManager _manager;
        private bool _srpActive;

        /// <summary>
        /// Stores the manager reference so callbacks can forward drawing requests.
        /// </summary>
        public void Initialize(LootOverlayManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// Subscribes to render callbacks depending on whether SRP is active.
        /// </summary>
        private void OnEnable()
        {
            _srpActive = GraphicsSettings.renderPipelineAsset != null;
            Camera.onPostRender += OnPostRenderBuiltin;
            RenderPipelineManager.endCameraRendering += OnEndCameraRenderingSRP;
        }

        /// <summary>
        /// Unsubscribes from all render callbacks when disabled or destroyed.
        /// </summary>
        private void OnDisable()
        {
            Camera.onPostRender -= OnPostRenderBuiltin;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRenderingSRP;
        }

        /// <summary>
        /// Handles legacy pipeline post-render events.
        /// </summary>
        private void OnPostRenderBuiltin(Camera cam)
        {
            if (_srpActive)
                return;
            _manager?.DrawForCamera(cam);
        }

        /// <summary>
        /// Handles Scriptable Render Pipeline callbacks and forwards drawing to the manager.
        /// </summary>
        private void OnEndCameraRenderingSRP(ScriptableRenderContext ctx, Camera cam)
        {
            _manager?.DrawForCamera(cam);
        }
    }
}
