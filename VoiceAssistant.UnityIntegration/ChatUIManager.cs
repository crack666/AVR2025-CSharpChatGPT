using UnityEngine;
using UnityEngine.UI;
using System;

namespace VoiceAssistant.UnityIntegration
{
    /// <summary>
    /// Manages chat UI in Unity, instantiates message bubbles and hooks up playback controls.
    /// </summary>
    public class ChatUIManager : MonoBehaviour
    {
    [Header("References")]
    [SerializeField] private VoicePlaybackManager playbackManager;
        [SerializeField] private GameObject chatBubblePrefab; // Prefab with Text and Stop Button
        [SerializeField] private Transform chatContainer;

        /// <summary>
        /// Adds a user message bubble to the chat.
        /// </summary>
        public void AddUserMessage(string message)
        {
            var bubble = Instantiate(chatBubblePrefab, chatContainer);
            var text = bubble.GetComponentInChildren<Text>();
            text.text = message;
            // Hide stop button for user messages
            var stopBtn = bubble.transform.Find("StopButton")?.GetComponent<Button>();
            if (stopBtn) stopBtn.gameObject.SetActive(false);
        }

        /// <summary>
        /// Adds a bot message bubble with voice playback and stop control.
        /// </summary>
        public void AddBotMessage(string message, AudioClip voiceClip)
        {
            var bubble = Instantiate(chatBubblePrefab, chatContainer);
            var text = bubble.GetComponentInChildren<Text>();
            text.text = message;
            // Generate a unique ID for this audio clip
            var clipId = Guid.NewGuid().ToString();
            // Configure stop button to skip this clip
            var stopBtn = bubble.transform.Find("StopButton")?.GetComponent<Button>();
            if (stopBtn)
            {
                stopBtn.gameObject.SetActive(true);
                stopBtn.onClick.AddListener(() => playbackManager.SkipClip(clipId));
            }
            // Enqueue the clip with its ID for targeted playback
            playbackManager.EnqueueClip(clipId, voiceClip);
        }
    }
}