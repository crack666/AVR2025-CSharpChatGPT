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
    }
}