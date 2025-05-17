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
        /// Adds a new message to the log and notifies subscribers.
        /// </summary>
        /// <param name="role">Role of the message sender.</param>
        /// <param name="content">Text content of the message.</param>
        /// <returns>The created ChatMessage object.</returns>
        public ChatMessage AddMessage(ChatRole role, string content)
        {
            var message = new ChatMessage(Guid.NewGuid(), role, content, DateTime.UtcNow);
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
    }
}