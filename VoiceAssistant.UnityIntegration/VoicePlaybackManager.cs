using System.Collections.Generic;
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
        // Queue of items to play: each with an identifier for targeted control
        private readonly Queue<PlaybackItem> _clipQueue = new Queue<PlaybackItem>();
        private PlaybackItem _currentItem;

        // Represents an audio clip with its playback identifier
        private class PlaybackItem
        {
            public string Id { get; }
            public AudioClip Clip { get; }
            public PlaybackItem(string id, AudioClip clip)
            {
                Id = id;
                Clip = clip;
            }
        }

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
        }

        // Immediate playback has been replaced by EnqueueClip with identifiers.

        /// <summary>
        /// Stops all playback and clears the queue.
        /// </summary>
        public void Stop()
        {
            _clipQueue.Clear();
            _currentItem = null;
            _audioSource.Stop();
        }
        
        /// <summary>
        /// Enqueues an audio clip with the given identifier for sequential playback.
        /// </summary>
        public void EnqueueClip(string id, AudioClip clip)
        {
            if (string.IsNullOrEmpty(id) || clip == null) return;
            var item = new PlaybackItem(id, clip);
            _clipQueue.Enqueue(item);
            if (!_audioSource.isPlaying)
            {
                PlayNextInQueue();
            }
        }
        
        /// <summary>
        /// Skips any queued or currently playing clip matching the given identifier.
        /// </summary>
        public void SkipClip(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            // Filter out queued items with the same id
            var filtered = new Queue<PlaybackItem>();
            foreach (var item in _clipQueue)
            {
                if (item.Id != id) filtered.Enqueue(item);
            }
            _clipQueue.Clear();
            foreach (var item in filtered) _clipQueue.Enqueue(item);
            // If the current item matches, stop and play next
            if (_currentItem != null && _currentItem.Id == id)
            {
                _audioSource.Stop();
                _currentItem = null;
                PlayNextInQueue();
            }
        }
        
        /// <summary>
        /// Jump to and play the first queued clip matching the given identifier,
        /// removing all other queued items.
        /// </summary>
        public void PlayClip(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            // Find the first matching item in queue
            var targetQueue = new Queue<PlaybackItem>();
            foreach (var item in _clipQueue)
            {
                if (item.Id == id)
                {
                    targetQueue.Enqueue(item);
                    break;
                }
            }
            _clipQueue.Clear();
            foreach (var item in targetQueue) _clipQueue.Enqueue(item);
            // Stop current playback and start target
            _audioSource.Stop();
            _currentItem = null;
            PlayNextInQueue();
        }
        
        private void Update()
        {
            // When current finishes, advance to next in queue
            if (!_audioSource.isPlaying && _clipQueue.Count > 0)
            {
                PlayNextInQueue();
            }
        }
        
        private void PlayNextInQueue()
        {
            if (_clipQueue.Count == 0) return;
            _currentItem = _clipQueue.Dequeue();
            _audioSource.clip = _currentItem.Clip;
            _audioSource.Play();
        }
    }
}