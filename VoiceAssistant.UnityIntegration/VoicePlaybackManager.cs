using UnityEngine;

namespace VoiceAssistant.UnityIntegration
{
    /// <summary>
    /// Manages audio playback in Unity using a single AudioSource.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class VoicePlaybackManager : MonoBehaviour
    {
        private AudioSource _audioSource;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
        }

        /// <summary>Plays the given audio clip, interrupting any current playback.</summary>
        public void Play(AudioClip clip)
        {
            _audioSource.Stop();
            _audioSource.clip = clip;
            _audioSource.Play();
        }

        /// <summary>Stops any current playback.</summary>
        public void Stop()
        {
            _audioSource.Stop();
        }
    }
}