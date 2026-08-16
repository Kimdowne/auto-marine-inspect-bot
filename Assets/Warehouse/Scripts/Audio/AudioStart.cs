using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

namespace Unity.Templates.IndustryFundamentals
{
    public class AudioStart : MonoBehaviour
    {
        public AudioSource[] AudioSources;
        public AudioMixer Mixer;
        public string VolumeParameter = "MasterVol";

        private void Start()
        {
            foreach (AudioSource audioSource in AudioSources) audioSource.PlayDelayed(Random.Range(1f, 2f));

            StartCoroutine(FadeMasterVolume(-80f, 0f, 1f));
        }

        private IEnumerator FadeMasterVolume(float startDb, float endDb, float duration)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                float volume = Mathf.Lerp(startDb, endDb, t);
                Mixer.SetFloat(VolumeParameter, volume);
                yield return null;
            }

            Mixer.SetFloat(VolumeParameter, endDb);
        }
    }
}