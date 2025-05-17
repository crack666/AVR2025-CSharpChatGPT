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
        /// <param name="contentType">MIME content type of the audio.</param>
        /// <param name="fileName">Filename (with extension) of the audio file.</param>
        /// <returns>Recognized text.</returns>
        Task<string> RecognizeAsync(Stream audioStream, string contentType, string fileName);
    }
}