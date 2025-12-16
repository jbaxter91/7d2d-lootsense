using System;
using UnityEngine;

/// <summary>
/// Builds the lightweight IMGUI HUD that surfaces scan/render timing metrics.
/// </summary>
internal sealed class LootSenseProfilerDisplay
{
    private readonly Func<string> _textProvider;
    private LootSenseProfilerBehaviour _behaviour;

    /// <summary>
    /// Creates the profiler display wrapper with a delegate that supplies HUD text.
    /// </summary>
    public LootSenseProfilerDisplay(Func<string> textProvider)
    {
        _textProvider = textProvider;
    }

    /// <summary>
    /// Turns the HUD on or off by creating or destroying the backing behaviour.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            EnsureBehaviour();
        }
        else
        {
            DisableBehaviour();
        }
    }

    /// <summary>
    /// Indicates whether the HUD behaviour currently exists.
    /// </summary>
    public bool IsEnabled => _behaviour != null;

    /// <summary>
    /// Spawns the HUD GameObject once so repeated toggles do not leak duplicates.
    /// </summary>
    private void EnsureBehaviour()
    {
        if (_behaviour != null)
            return;

        var go = new GameObject("PerceptionMasteryLootSenseProfilerHUD")
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        UnityEngine.Object.DontDestroyOnLoad(go);
        _behaviour = go.AddComponent<LootSenseProfilerBehaviour>();
        _behaviour.Initialize(_textProvider, OnHudDestroyed);
    }

    /// <summary>
    /// Shuts down and clears the HUD behaviour instance.
    /// </summary>
    private void DisableBehaviour()
    {
        if (_behaviour == null)
            return;

        _behaviour.Shutdown();
        _behaviour = null;
    }

    /// <summary>
    /// Callback invoked once the behaviour destroys itself so the wrapper can clear references.
    /// </summary>
    private void OnHudDestroyed()
    {
        _behaviour = null;
    }

    /// <summary>
    /// MonoBehaviour responsible for actually drawing the HUD each frame.
    /// </summary>
    private sealed class LootSenseProfilerBehaviour : MonoBehaviour
    {
        private Func<string> _textProvider;
        private Action _onDestroyed;
        private GUIStyle _labelStyle;
        private GUIStyle _backgroundStyle;

        /// <summary>
        /// Supplies the text provider and destruction callback after the behaviour is instantiated.
        /// </summary>
        public void Initialize(Func<string> textProvider, Action onDestroyed)
        {
            _textProvider = textProvider;
            _onDestroyed = onDestroyed;
        }

        /// <summary>
        /// Destroys the HUD GameObject safely.
        /// </summary>
        public void Shutdown()
        {
            if (this != null)
                Destroy(gameObject);
        }

        /// <summary>
        /// Notifies the wrapper that the HUD instance has been destroyed.
        /// </summary>
        private void OnDestroy()
        {
            _onDestroyed?.Invoke();
            _onDestroyed = null;
        }

        /// <summary>
        /// Lazily builds the GUI styles so the HUD has consistent visuals.
        /// </summary>
        private void EnsureStyles()
        {
            if (_labelStyle != null)
                return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(0.85f, 0.95f, 1f, 1f) }
            };

            _backgroundStyle = new GUIStyle(GUI.skin.box);
        }

        /// <summary>
        /// Draws the profiler string using IMGUI each frame.
        /// </summary>
        private void OnGUI()
        {
            if (_textProvider == null)
                return;

            string text = _textProvider();
            if (string.IsNullOrEmpty(text))
                return;

            EnsureStyles();

            var rect = new Rect(20f, 140f, 360f, 140f);
            Color prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.Box(rect, GUIContent.none, _backgroundStyle);
            GUI.color = prevColor;
            GUI.Label(rect, text, _labelStyle);
        }
    }
}
