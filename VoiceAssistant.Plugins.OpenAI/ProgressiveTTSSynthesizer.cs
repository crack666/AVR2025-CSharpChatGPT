using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;

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
        }

        /// <summary>
        /// Implements the ISynthesizer.SynthesizeTextChunkAsync method for single-shot TTS.
        /// Synthesizes a single text chunk without chunking/segmentation.
        /// </summary>
        /// <param name="textChunk">Text chunk to synthesize.</param>
        /// <param name="voice">Voice identifier.</param>
        /// <returns>Raw audio bytes for this text chunk.</returns>
        /*public Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice)
            => SynthesizeAsync(textChunk, voice);*/

        public async Task<byte[]> SynthesizeTextChunkAsync(string textChunk, string voice)
        {
            if (string.IsNullOrWhiteSpace(textChunk))
                throw new ArgumentException("Text chunk cannot be empty.", nameof(textChunk));

            byte[] lastReceived = null;

            // ChunkedSynthesisAsync erwartet üblicherweise (text, voice, onChunkReceived)
            await ChunkedSynthesisAsync(textChunk, voice, chunkBytes =>
            {
                // Wir überschreiben lastReceived bei jeder Teilantwort
                lastReceived = chunkBytes;
            });

            // Am Ende enthält lastReceived das komplette, zuletzt zurückgegebene Audio
            return lastReceived
                   ?? throw new InvalidOperationException("No audio data was returned from ChunkedSynthesisAsync.");
        }

        /// <summary>
        /// Regular implementation of ISynthesizer interface for backward compatibility.
        /// Synthesizes the complete text in a single request.
        /// </summary>
        /// <param name="text">The text to synthesize.</param>
        /// <param name="voice">The voice to use.</param>
        /// <returns>Audio bytes representing the synthesized speech.</returns>
        public async Task<byte[]> SynthesizeAsync(string text, string voice)
        {
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
                throw new ArgumentException("Text input for speech synthesis cannot be empty.", nameof(text));
            }

            text = text.Trim();
            if (text.Length == 0)
            {
                throw new ArgumentException("Trimmed text input is empty.", nameof(text));
            }

            // For very short text, just do a single synthesis
            if (text.Length < MinTextLength)
            {
                LogDebug($"Text too short ({text.Length} chars), using single synthesis");
                var audioBytes = await SynthesizeAsync(text, voice);
                onChunkReady(audioBytes);
                return;
            }

            LogDebug($"Starting sentence-based chunked synthesis of {text.Length} characters with voice {voice}");

            // Split text into natural language chunks at sentence boundaries
            var chunks = SplitTextIntoSentenceChunks(text);
            LogDebug($"Split text into {chunks.Count} sentence chunks");

            // Process each chunk sequentially to maintain order
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                LogDebug($"Synthesizing chunk {i + 1}/{chunks.Count}: \"{ShortenForLog(chunk)}\" ({chunk.Length} chars)");

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

                    // Add a slight pause at the end of chunks that don't end with punctuation
                    /*string processedChunk = chunk;
                    if (i < chunks.Count - 1 && !EndsWithPunctuation(chunk))
                    {
                        processedChunk = chunk + ",";
                        LogDebug($"Added comma to chunk for natural pause");
                    }

                    var payload = new { model = "tts-1", voice = voice, input = processedChunk };*/
                    var payload = new { model = "tts-1", voice = voice, input = chunk };
                    var body = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    var bytes = await response.Content.ReadAsByteArrayAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        var msg = Encoding.UTF8.GetString(bytes);
                        LogDebug($"Chunk synthesis error: {msg}");
                        throw new ApplicationException($"TTS failed for chunk {i + 1}: {msg}");
                    }

                    // Invoke callback with the synthesized audio for this chunk
                    LogDebug($"Chunk {i + 1} synthesized: {bytes.Length} bytes");
                    onChunkReady(bytes);
                }
                catch (Exception ex)
                {
                    LogDebug($"Error synthesizing chunk {i + 1}: {ex.Message}");
                    throw;
                }
            }

            LogDebug("Chunked synthesis completed");
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

            // Use a regex pattern that recognizes sentence endings while handling special cases
            // like abbreviations, ellipses, and quoted text
            var sentencePattern = new Regex(

                 //einfacher
                 @"[^.!?]*[.!?](?=\s|$)",
                 RegexOptions.Singleline | RegexOptions.IgnoreCase
            );

            var matches = sentencePattern.Matches(input);

            // Extract complete sentences
            int lastIndex = 0;
            foreach (Match match in matches)
            {
                string sentence = match.Value.Trim();
                if (!string.IsNullOrEmpty(sentence))
                {
                    LogDebug($"Found sentence: '{ShortenForLog(sentence)}'");
                    chunks.Add(sentence);
                }
                lastIndex = match.Index + match.Length;
            }

            // Handle any remaining text after the last sentence
            if (lastIndex < input.Length)
            {
                string remainder = input.Substring(lastIndex).Trim();
                if (!string.IsNullOrEmpty(remainder))
                {
                    LogDebug($"Processing remaining text: '{ShortenForLog(remainder)}'");
                    chunks.Add(remainder);
                }
            }

            // Combine very short chunks for better audio quality
            for (int i = chunks.Count - 2; i >= 0; i--)
            {
                if (chunks[i].Length + chunks[i + 1].Length < 50)
                {
                    LogDebug($"Combining short chunks for better audio flow");
                    chunks[i] = chunks[i] + " " + chunks[i + 1];
                    chunks.RemoveAt(i + 1);
                }
            }

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