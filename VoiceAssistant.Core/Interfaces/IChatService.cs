using System.Collections.Generic;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Core.Interfaces
{
    /// <summary>
    /// Interface for chat (LLM) service implementations with context support.
    /// </summary>
    public interface IChatService
    {
        /// <summary>
        /// Generates a response based on the given chat history.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user  bot).</param>
        /// <returns>Generated response text.</returns>
        Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory);
    }
}