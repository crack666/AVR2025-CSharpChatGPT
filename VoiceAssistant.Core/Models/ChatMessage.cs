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

        public string? Model { get; set; }
        public string? Voice { get; set; }
        public ChatMessage(Guid id, ChatRole role, string content, DateTime timestamp, string? model = null, string? voice = null)
        {
            Id = id;
            Role = role;
            Content = content;
            Timestamp = timestamp;
            Model = model;
            Voice = voice;
        }
    }
}