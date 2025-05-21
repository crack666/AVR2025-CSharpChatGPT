using System.Threading.Tasks;

namespace VoiceAssistant.Core.Interfaces
{
    /// <summary>
    /// Interface for text-to-speech synthesizer implementations.
    /// </summary>
    public interface ISynthesizer
    {
        /// <summary>
        /// Synthesizes speech audio bytes from the given text and voice.
        /// </summary>
        /// <param name="text">Input text to synthesize.</param>
        /// <param name="voice">Voice identifier.</param>
        /// <returns>Raw audio bytes (e.g., MP3).</returns>
        Task<byte[]> SynthesizeAsync(string text, string voice);
        
        /// <summary>
        /// Synthesizes speech audio for a partial text chunk (for progressive streaming).
        /// </summary>
        /// <param name="textChunk">Text chunk to synthesize.</param>
        /// <param name="voice">Voice identifier.</param>
        /// <returns>Raw audio bytes for this text chunk.</returns>
        Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice);
    }
}