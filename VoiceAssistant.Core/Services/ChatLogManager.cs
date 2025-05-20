using System;
using System.Collections.Generic;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Core.Services
{
    /// <summary>
    /// Manages chat message history in FIFO order with event notifications.
    /// </summary>
    public class ChatLogManager
    {
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();
        private readonly object _lock = new object();

        /// <summary>
        /// Invoked when a new message is added to the log.
        /// </summary>
        public event Action<ChatMessage>? MessageAdded;

        /// <summary>
        /// Invoked when the chat history is cleared.
        /// </summary>
        public event Action? MessagesCleared;

        /// <summary>
        /// Adds a new message to the log and notifies subscribers.
        /// </summary>
        /// <param name="role">Role of the message sender.</param>
        /// <param name="content">Text content of the message.</param>
        /// <returns>The created ChatMessage object.</returns>
        /// <summary>
        /// Adds a new message without model/voice metadata.
        /// </summary>
        public ChatMessage AddMessage(ChatRole role, string content)
            => AddMessage(role, content, model: null, voice: null);

        /// <summary>
        /// Adds a new message with optional model/voice metadata.
        /// </summary>
        public ChatMessage AddMessage(ChatRole role, string content, string? model, string? voice)
        {
            var message = new ChatMessage(Guid.NewGuid(), role, content, DateTime.UtcNow, model, voice);
            lock (_lock)
            {
                _messages.Add(message);
            }
            MessageAdded?.Invoke(message);
            return message;
        }

        /// <summary>
        /// Returns a read-only snapshot of all messages in the log.
        /// </summary>
        public IReadOnlyList<ChatMessage> GetMessages()
        {
            lock (_lock)
            {
                return _messages.AsReadOnly();
            }
        }

        /// <summary>
        /// Clears all messages from the chat history and notifies subscribers.
        /// </summary>
        public void ClearMessages()
        {
            lock (_lock)
            {
                _messages.Clear();
            }
            MessagesCleared?.Invoke();
        }
    }
}