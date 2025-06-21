#nullable enable
using System.IO;
using System.Threading.Tasks;

namespace VoiceAssistant.Core.Interfaces
{
    /// <summary>
    /// Interface for speech-to-text recognizer implementations.
    /// </summary>
    public interface IRecognizer
    {
        /// <summary>
        /// Recognizes speech from the provided audio stream.
        /// </summary>
        /// <param name="audioStream">Stream containing audio data.</param>
        /// <param name="language">Language of the audio content.</param>
        /// <param name="contentType">Optional. The MIME type of the audio stream (e.g., "audio/wav", "audio/mpeg").</param>
        /// <param name="fileName">Optional. The name of the file, used by some APIs for multipart form data.</param>
        /// <returns>Recognized text.</returns>
        Task<string> RecognizeAsync(Stream audioStream, string language, string? contentType = null, string? fileName = null);

        /// <summary>
        /// Streaming recognition that processes audio chunks as they become available.
        /// This enables faster response times by starting processing before speech ends.
        /// </summary>
        /// <param name="audioChunk">Audio data chunk to process</param>
        /// <param name="language">Language hint for recognition</param>
        /// <param name="isPartial">Whether this is a partial chunk (more audio expected) or final</param>
        /// <returns>Recognized text, may be partial if isPartial=true</returns>
        Task<string> RecognizeStreamingAsync(byte[] audioChunk, string language, bool isPartial = true);

        /// <summary>
        /// Real-time recognition using OpenAI Realtime API.
        /// This establishes a WebSocket connection for true real-time processing.
        /// </summary>
        /// <param name="audioChunk">Audio data chunk</param>
        /// <param name="language">Language hint</param>
        /// <param name="sessionId">Session identifier for tracking</param>
        /// <returns>Real-time recognition result</returns>
        Task<string> RecognizeRealtimeAsync(byte[] audioChunk, string language, string sessionId);

        /// <summary>
        /// Connect to real-time recognition service (if supported).
        /// </summary>
        /// <param name="sessionId">Session identifier</param>
        /// <param name="language">Language hint</param>
        Task ConnectAsync(string sessionId, string language = "en");

        /// <summary>
        /// Disconnect from real-time recognition service (if connected).
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Check if real-time API is connected.
        /// </summary>
        bool IsRealtimeConnected { get; }
    }
}