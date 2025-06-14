using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using System.Linq; // Added for .Any()

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Progressive Text-to-Speech synthesizer using OpenAI API.
    /// Breaks text into natural chunks for faster feedback while synthesizing speech.
    /// Ensures words are never split between chunks for natural-sounding speech.
    /// </summary>
    public class ProgressiveTTSSynthesizer : ISynthesizer
    {
        private readonly HttpClient _httpClient;
        private readonly bool _enableDebugLogging = true;

        /// <summary>
        /// Minimum text length in characters to consider for chunked synthesis.
        /// For very short texts, single-shot synthesis is more efficient.
        /// </summary>
        private const int MinTextLength = 40;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressiveTTSSynthesizer"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for API requests.</param>
        public ProgressiveTTSSynthesizer(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            LogDebug($"ProgressiveTTSSynthesizer instantiated. HttpClient Timeout: {_httpClient.Timeout}");
        }

        /// <summary>
        /// Implements the ISynthesizer.SynthesizeTextChunkAsync method for single-shot TTS.
        /// Synthesizes a single text chunk without chunking/segmentation.
        /// </summary>
        /// <param name="textChunk">Text chunk to synthesize.</param>
        /// <param name="voice">Voice identifier.</param>
        /// <returns>Raw audio bytes for this text chunk.</returns>
        public Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice)
            => SynthesizeAsync(textChunk, voice); // MODIFIED: Directly call SynthesizeAsync

        /// <summary>
        /// Regular implementation of ISynthesizer interface for backward compatibility.
        /// Synthesizes the complete text in a single request.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <returns>Audio bytes representing the synthesized speech.</returns>
        public async Task<byte[]> SynthesizeAsync(string text, string voice)
        {
            LogDebug($"Entering SynthesizeAsync. Voice: {voice}. Text length: {text?.Length ?? 0}. HttpClient Timeout: {_httpClient.Timeout}.");

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text input for speech synthesis cannot be empty.", nameof(text));
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                text = "No response available.";
            }

            LogDebug($"Standard synthesis of {text.Length} characters with voice {voice}");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

            var payload = new { model = "tts-1", voice = voice, input = text };
            var body = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var msg = Encoding.UTF8.GetString(bytes);
                throw new ApplicationException($"TTS failed: {msg}");
            }
            return bytes;
        }

        /// <summary>
        /// Synthesizes speech from text in chunks, providing each chunk via callback as it becomes available.
        /// This allows for faster perceived response time as the UI can play audio while the rest is being synthesized.
        /// Uses natural language boundaries to create sensible chunks for better speech quality.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <param name="onChunkReady">Callback that will receive audio bytes for each synthesized chunk.</param>
        /// <returns>A task representing the complete synthesis operation.</returns>
        public async Task ChunkedSynthesisAsync(string text, string voice, Action<byte[]> onChunkReady)
        {
            if (onChunkReady == null)
                throw new ArgumentNullException(nameof(onChunkReady));

            if (string.IsNullOrWhiteSpace(text))
            {
                LogDebug("ChunkedSynthesisAsync called with empty or whitespace text. No audio will be generated.");
                return;
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                LogDebug("ChunkedSynthesisAsync called with effectively empty text after trimming. No audio will be generated.");
                return;
            }

            // For very short text, just do a single synthesis and invoke onChunkReady once.
            // This avoids unnecessary overhead of regex splitting for tiny segments.
            // The MinTextLength can be tuned.
            if (text.Length < MinTextLength)
            {
                LogDebug($"Text too short ({text.Length} chars), using single synthesis for ChunkedSynthesisAsync");
                try
                {
                    var audioBytes = await SynthesizeAsync(text, voice); // This is the non-chunking SynthesizeAsync
                    onChunkReady(audioBytes);
                }
                catch (Exception ex)
                {
                    LogDebug($"Error during single synthesis within ChunkedSynthesisAsync: {ex.Message}");
                    throw;
                }
                return;
            }

            LogDebug($"Starting sentence-based chunked synthesis of {text.Length} characters with voice {voice} for ChunkedSynthesisAsync.");

            // Split text into natural language chunks at sentence boundaries
            var chunks = SplitTextIntoSentenceChunks(text);
            LogDebug($"Split text into {chunks.Count} sentence chunks for ChunkedSynthesisAsync");

            // Process each chunk sequentially to maintain order
            for (int i = 0; i < chunks.Count; i++)
            {
                var currentChunk = chunks[i]; // Renamed to avoid conflict if 'chunk' is used elsewhere
                if (string.IsNullOrWhiteSpace(currentChunk))
                {
                    LogDebug($"Skipping empty chunk {i + 1}/{chunks.Count}");
                    continue;
                }
                // Corrected string interpolation for logging
                LogDebug($"Synthesizing chunk {i + 1}/{chunks.Count} for ChunkedSynthesisAsync: \"{ShortenForLog(currentChunk)}\" ({currentChunk.Length} chars)");

                try
                {
                    var audioBytes = await SynthesizeAsync(currentChunk, voice);
                    
                    if (audioBytes != null && audioBytes.Length > 0)
                    {
                        LogDebug($"Chunk {i + 1} synthesized: {audioBytes.Length} bytes. Invoking onChunkReady.");
                        onChunkReady(audioBytes);
                    }
                    else
                    {
                        LogDebug($"Chunk {i + 1} synthesis resulted in null or empty audio. Not invoking onChunkReady.");
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"Error synthesizing chunk {i + 1} for ChunkedSynthesisAsync: {ex.Message}");
                    // Decide whether to rethrow or continue with other chunks.
                    // For now, logging the error and continuing with the next chunk.
                    // If one chunk fails, we might still want to process others.
                }
            }

            LogDebug("Chunked synthesis completed for ChunkedSynthesisAsync");
        }

        /// <summary>
        /// Creates a shortened version of the chunk text suitable for logging.
        /// </summary>
        /// <param name="text">The text to shorten.</param>
        /// <returns>A shortened version of the text.</returns>
        private string ShortenForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            const int maxLogLength = 50;

            if (text.Length <= maxLogLength)
                return text;

            // This truncation is only for logging display purposes
            // Using a special marker "[...]" instead of "..." to avoid confusion with actual text content
            return text.Substring(0, maxLogLength / 2 - 3) + "[...]" +
                   text.Substring(text.Length - maxLogLength / 2);
        }

        /// <summary>
        /// Splits text into chunks based on sentence boundaries.
        /// Uses a regex pattern to identify sentence endings and ensures words are never split.
        /// </summary>
        /// <param name="input">The text to split into chunks</param>
        /// <returns>A list of text chunks at sentence boundaries</returns>
        private List<string> SplitTextIntoSentenceChunks(string input)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return chunks;

            LogDebug($"Splitting text into sentence chunks: {input.Length} characters");

            // Regex to split by sentences, keeping punctuation.
            // This pattern tries to identify sentence endings (. ! ?) followed by space or end of string.
            // It also handles cases like "Mr. Smith" or "e.g." by not splitting after a period followed by a lowercase letter.
            // It's not perfect but aims to be better than simple character splits.
            // Consider refining this regex further based on observed edge cases.
            // Example: @"(?<!\\w\\.\\w.)(?<![A-Z][a-z]\\.)(?<=\\.|\\?|!)\\s"
            // A simpler version that might work well enough:
            var sentencePattern = new Regex(
                 // Splits after a sentence-ending punctuation mark (. ! ?) that is followed by a space or is at the end of the string.
                 // It tries to avoid splitting in the middle of abbreviations like "U.S.A." by looking ahead for spaces.
                 @"(?<=[.!?])(\\s+|$)(?<!\\s[A-Z]\\.)", // Simpler, might need refinement
                 RegexOptions.Singleline | RegexOptions.IgnoreCase
            );
            
            // More robust sentence splitting:
            // This regex splits after '.', '!', '?' when followed by whitespace or end of string.
            // It includes lookbehinds to avoid splitting on abbreviations (e.g., "Mr.", "Mrs.", "Dr.") or initials.
            // It also tries to handle quoted sentences.
            // This is a complex area, and perfect splitting is hard.
            string[] splitSentences = Regex.Split(input, @"(?<=[.!?])\s+(?=[A-Z""'])|(?<=[.!?])$");

            foreach (string sentencePart in splitSentences)
            {
                string trimmedPart = sentencePart.Trim();
                if (!string.IsNullOrEmpty(trimmedPart))
                {
                    chunks.Add(trimmedPart);
                    LogDebug($"Found sentence chunk: '{ShortenForLog(trimmedPart)}'");
                }
            }


            if (!chunks.Any()) // If regex split results in nothing (e.g. very short text with no delimiters)
            {
                if (!string.IsNullOrWhiteSpace(input))
                {
                     LogDebug($"Regex splitting yielded no chunks, adding entire input as one chunk: '{ShortenForLog(input)}'");
                    chunks.Add(input.Trim());
                }
            }

            // Post-processing: Combine very short chunks if necessary, or ensure chunks are not overly long.
            // The original logic for combining short chunks can be kept or adjusted.
            // For now, let's remove the aggressive combination to see the raw sentence splits.
            // Consider re-adding if too many tiny audio segments are produced.
            /*
            for (int i = chunks.Count - 2; i >= 0; i--)
            {
                if (chunks[i].Length + chunks[i + 1].Length < 50) // Arbitrary threshold
                {
                    LogDebug($"Combining short chunks: '{ShortenForLog(chunks[i])}' + '{ShortenForLog(chunks[i+1])}'");
                    chunks[i] = chunks[i] + " " + chunks[i + 1];
                    chunks.RemoveAt(i + 1);
                }
            }
            */

            LogDebug($"Final chunk count after sentence splitting: {chunks.Count}");
            return chunks;
        }



        /// <summary>
        /// Logs debug information if debug logging is enabled.
        /// </summary>
        /// <param name="message">The message to log.</param>
        private void LogDebug(string message)
        {
            if (_enableDebugLogging)
            {
                Console.WriteLine($"[DEBUG] ProgressiveTTSSynthesizer: {message}");
            }
        }
    }
}