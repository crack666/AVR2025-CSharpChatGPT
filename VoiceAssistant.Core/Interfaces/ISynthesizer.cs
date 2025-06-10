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

        /// <summary>
        /// Synthesizes speech from text in chunks, providing each chunk via callback as it becomes available.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <param name="onChunkReady">Callback that will receive audio bytes for each synthesized chunk.</param>
        /// <returns>A task representing the complete synthesis operation.</returns>
        Task ChunkedSynthesisAsync(string text, string voice, System.Action<byte[]> onChunkReady);
    }
}