using System;
using UnityEngine;

internal sealed class LootSenseProfilerDisplay
{
    private readonly Func<string> _textProvider;
    private LootSenseProfilerBehaviour _behaviour;

    public LootSenseProfilerDisplay(Func<string> textProvider)
    {
        _textProvider = textProvider;
    }

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

    public bool IsEnabled => _behaviour != null;

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

    private void DisableBehaviour()
    {
        if (_behaviour == null)
            return;

        _behaviour.Shutdown();
        _behaviour = null;
    }

    private void OnHudDestroyed()
    {
        _behaviour = null;
    }

    private sealed class LootSenseProfilerBehaviour : MonoBehaviour
    {
        private Func<string> _textProvider;
        private Action _onDestroyed;
        private GUIStyle _labelStyle;
        private GUIStyle _backgroundStyle;

        public void Initialize(Func<string> textProvider, Action onDestroyed)
        {
            _textProvider = textProvider;
            _onDestroyed = onDestroyed;
        }

        public void Shutdown()
        {
            if (this != null)
                Destroy(gameObject);
        }

        private void OnDestroy()
        {
            _onDestroyed?.Invoke();
            _onDestroyed = null;
        }

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
