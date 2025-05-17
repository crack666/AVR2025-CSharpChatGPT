using System;

namespace VoiceAssistant.Core.Models
{
    /// <summary>
    /// Represents a single chat message with timestamp and role.
    /// </summary>
    public class ChatMessage
    {
        public Guid Id { get; }
        public ChatRole Role { get; }
        public string Content { get; }
        public DateTime Timestamp { get; }

        public ChatMessage(Guid id, ChatRole role, string content, DateTime timestamp)
        {
            Id = id;
            Role = role;
            Content = content;
            Timestamp = timestamp;
        }
    }
}