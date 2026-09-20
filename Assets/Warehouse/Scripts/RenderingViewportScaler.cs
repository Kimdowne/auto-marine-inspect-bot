using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Templates.IndustryFundamentals
{
    [ExecuteAlways]
    public class RenderingViewportScaler : MonoBehaviour
    {
        private Camera _camera;
        private VisualElement _secCamVisualElement;
        private VisualElement _panel;
        private float _panelWidth;
        private float _panelHeight;
        private UIDocument _uiDocument;

        private void Start()
        {
            _camera = GetComponent<Camera>();
            _camera.hideFlags = HideFlags.DontSaveInEditor;
            _uiDocument = FindAnyObjectByType<UIDocument>();
            FindUIReferences();
        }

        private void FindUIReferences()
        {
            _panel = _uiDocument.rootVisualElement;
            _secCamVisualElement = _panel.Q<VisualElement>("SecurityCam");
        }

        private bool UIRefsAreValid => _panel != null || _secCamVisualElement != null;

        void Update()
        {
            // This extra check wouldn't be necessary if this script wasn't executing at Edit time with [ExecuteAlways]
            if (!UIRefsAreValid) FindUIReferences();
            if (!UIRefsAreValid) return;

            _panelWidth = _panel.layout.width;
            _panelHeight = _panel.layout.height;
        
            Rect worldBound = _secCamVisualElement!.worldBound;

            Rect cameraRect = new(
                worldBound.x / _panelWidth,
                (_panelHeight - worldBound.yMax) / _panelHeight,
                worldBound.width / _panelWidth,
                worldBound.height / _panelHeight
            );
        
            if(float.IsNaN(cameraRect.width) || float.IsNaN(cameraRect.height)) return;

            _camera.rect = cameraRect;
        }

        private void OnDisable()
        {
            if(_camera != null) _camera.hideFlags = HideFlags.None;
        }
    }
}