using UnityEngine;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;

namespace VoiceAssistant.UnityIntegration
{
    /// <summary>
    /// Coordinates the chat flow: logs messages and updates the UI.
    /// </summary>
    public class ChatManager : MonoBehaviour
    {
        [Header("UI Manager")]
        [SerializeField] private ChatUIManager uiManager;

        private ChatLogManager _chatLogManager;

        private void Awake()
        {
            _chatLogManager = new ChatLogManager();
            _chatLogManager.MessageAdded += OnMessageAdded;
        }

        private void OnDestroy()
        {
            _chatLogManager.MessageAdded -= OnMessageAdded;
        }

        /// <summary>
        /// Called by UI when the user sends a message.
        /// </summary>
        /// <param name="text">User input text.</param>
        public void SendUserMessage(string text)
        {
            _chatLogManager.AddMessage(ChatRole.User, text);
            // TODO: Trigger ASR/LLM pipeline and add bot response via AddBotResponse
        }

        /// <summary>
        /// Adds a bot response to the log and plays associated audio.
        /// </summary>
        /// <param name="text">Bot response text.</param>
        /// <param name="voiceClip">Pre-generated audio clip or null.</param>
        public void AddBotResponse(string text, AudioClip voiceClip = null)
        {
            _chatLogManager.AddMessage(ChatRole.Bot, text);
            if (voiceClip != null)
                uiManager.AddBotMessage(text, voiceClip);
        }

        private void OnMessageAdded(ChatMessage message)
        {
            // Invoke on main thread; Unity calls this on main thread by default.
            if (message.Role == ChatRole.User)
                uiManager.AddUserMessage(message.Content);
            else if (message.Role == ChatRole.Bot)
                uiManager.AddBotMessage(message.Content, null);
        }
    }
}