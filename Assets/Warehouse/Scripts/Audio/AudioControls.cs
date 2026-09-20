using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;

namespace Unity.Templates.IndustryFundamentals
{
    public class AudioControls : MonoBehaviour
    {
        [SerializeField] private AudioClip uiClickSound;
        public AudioSource collisionSound; // AudioSource for collision sounds
        [SerializeField] private UIDocument uiDocument;
        [SerializeField] private AudioMixer audioMixer;
        public float collisionSoundCooldown = 0.5f; // Cooldown time in seconds
        private AudioSource audioSource;
        private Slider audioVolumeSlider;
        private float lastCollisionSoundTime = -999f;
        private readonly List<RobotDataSO> _cachedRobotData = new();

        private static AudioControls Instance { get; set; }

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
            else
                Destroy(gameObject);
        
            audioSource = GetComponent<AudioSource>();
        }

        private void OnEnable()
        {
            if (RobotManager.Instance != null) RobotManager.Instance.RobotListChanged += OnRobotListChanged;
            else Debug.LogWarning("AudioControls: RobotManager.Instance is null!");
        }

        private void Start()
        {
            RegisterClickEvents();
            StartCoroutine(RegisterInitialCards());
        }

        private IEnumerator RegisterInitialCards()
        {
            yield return null; // Wait one frame for UI to be fully built

            VisualElement root = uiDocument.rootVisualElement;
            ScrollView robotList = root.Q<ScrollView>("RobotsListView");

            foreach (VisualElement card in robotList.Children())
            {
                card.UnregisterCallback<ClickEvent>(OnUIClick);
                card.RegisterCallback<ClickEvent>(OnUIClick);
            }
        }

        private void OnDestroy()
        {
            if (RobotManager.Instance != null) RobotManager.Instance.RobotListChanged -= OnRobotListChanged;
            UnregisterRobotCollisionDetection();
        }

        private void OnRobotListChanged(List<Robot> robots)
        {
            RegisterRobotCollisionDetection(robots);

            // Wait a frame for UI to update, then register clicks on new cards
            StartCoroutine(RegisterRobotCardClicksDelayed());
        }

        private void RegisterRobotCollisionDetection(List<Robot> robots)
        {
            UnregisterRobotCollisionDetection();

            _cachedRobotData.Clear();

            foreach (Robot robot in robots)
            {
                _cachedRobotData.Add(robot.RobotData);
                _cachedRobotData[^1].RobotCollisionDetected += PlayCollisionSound;
            }
        }

        private void UnregisterRobotCollisionDetection()
        {
            foreach (RobotDataSO cachedRobotData in _cachedRobotData)
                cachedRobotData.RobotCollisionDetected -= PlayCollisionSound;
        }

        private IEnumerator RegisterRobotCardClicksDelayed()
        {
            yield return null; // Wait one frame for UI to be built

            VisualElement root = uiDocument.rootVisualElement;
            ScrollView robotList = root.Q<ScrollView>("RobotsListView");

            foreach (VisualElement card in robotList.Children())
            {
                card.UnregisterCallback<ClickEvent>(OnUIClick);
                card.RegisterCallback<ClickEvent>(OnUIClick);
            }
        }

        private void RegisterClickEvents()
        {
            VisualElement root = uiDocument.rootVisualElement;

            VisualElement autoButton = root.Q<VisualElement>("ModeAutoBtn");
            VisualElement manualButton = root.Q<VisualElement>("ModeManualBtn");
            autoButton.RegisterCallback<ClickEvent>(OnUIClick);
            manualButton.RegisterCallback<ClickEvent>(OnUIClick);

            audioVolumeSlider = root.Q<VisualElement>("Volume").Q<Slider>();

            if (audioMixer.GetFloat("GameVol", out float currentVolumeDb))
            {
                audioVolumeSlider.SetValueWithoutNotify(DbToSlider(currentVolumeDb));
            }

            audioVolumeSlider.RegisterValueChangedCallback(OnAudioVolumeChanged);
        }

        private void OnAudioVolumeChanged(ChangeEvent<float> evt)
        {
            audioMixer.SetFloat("GameVol", SliderToDb(evt.newValue));
        }

        // Slider -80..0 → normalize to 0..1 → apply 20*log10 for logarithmic taper
        // Result: halving the slider position = -6dB
        private static float SliderToDb(float sliderValue)
        {
            float normalized = (sliderValue + 80f) / 80f;
            if (normalized <= 0f) return -80f;
            return Mathf.Max(-80f, 20f * Mathf.Log10(normalized));
        }

        // Inverse: dB back to slider value
        private static float DbToSlider(float db)
        {
            if (db <= -80f) return -80f;
            float normalized = Mathf.Pow(10f, db / 20f); // 0..1
            return normalized * 80f - 80f; // back to -80..0 range
        }


        private void OnUIClick(ClickEvent evt)
        {
            audioSource.PlayOneShot(uiClickSound);
        }

        private void PlayCollisionSound(Vector3 position)
        {
            // Check if enough time has passed since the last collision sound
            if (Time.time - lastCollisionSoundTime < collisionSoundCooldown) return; // Still in cooldown, don't play sound

            if (collisionSound != null)
                try
                {
                    collisionSound.transform.position = position;
                    collisionSound.Play();
                    lastCollisionSoundTime = Time.time;
                }
                catch (Exception e)
                {
                    Debug.LogError($"AudioControls: Error playing collision sound: {e.Message}");
                }
            else
                Debug.LogWarning("AudioControls: collisionSound AudioSource is null!");
        }
    }
}