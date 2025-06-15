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
        /// Generates a response based on the given chat history and model.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user  bot).</param>
        /// <param name="chatModel">The chat model to use (e.g., "gpt-3.5-turbo", "gpt-4o").</param>
        /// <returns>Generated response text.</returns>
        Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory, string chatModel);
    }
}