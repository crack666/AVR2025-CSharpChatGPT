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
    /// </summary>
    public class ProgressiveTTSSynthesizer : ISynthesizer
    {
        private readonly HttpClient _httpClient;
        private readonly bool _enableDebugLogging = true;
        
        /// <summary>
        /// Minimum chunk size in characters to process as a separate unit.
        /// </summary>
        private const int MinChunkSize = 20;
        
        /// <summary>
        /// Maximum chunk size in characters to prevent excessive API calls.
        /// </summary>
        private const int MaxChunkSize = 300;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressiveTTSSynthesizer"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for API requests.</param>
        public ProgressiveTTSSynthesizer(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
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
            if (text.Length < MinChunkSize)
            {
                LogDebug($"Text too short ({text.Length} chars), using single synthesis");
                var audioBytes = await SynthesizeAsync(text, voice);
                onChunkReady(audioBytes);
                return;
            }
            
            LogDebug($"Starting chunked synthesis of {text.Length} characters with voice {voice}");
            
            // Split text into natural language chunks
            var chunks = SplitTextIntoNaturalChunks(text);
            LogDebug($"Split text into {chunks.Count} chunks");
            
            // Process each chunk sequentially
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                LogDebug($"Synthesizing chunk {i+1}/{chunks.Count}: {chunk.Length} chars");
                
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
                    
                    var payload = new { model = "tts-1", voice = voice, input = chunk };
                    var body = JsonSerializer.Serialize(payload);
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    
                    var response = await _httpClient.SendAsync(request);
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        var msg = Encoding.UTF8.GetString(bytes);
                        LogDebug($"Chunk synthesis error: {msg}");
                        throw new ApplicationException($"TTS failed for chunk {i+1}: {msg}");
                    }
                    
                    // Invoke callback with the synthesized audio for this chunk
                    LogDebug($"Chunk {i+1} synthesized: {bytes.Length} bytes");
                    onChunkReady(bytes);
                }
                catch (Exception ex)
                {
                    LogDebug($"Error synthesizing chunk {i+1}: {ex.Message}");
                    throw;
                }
            }
            
            LogDebug("Chunked synthesis completed");
        }
        
        /// <summary>
        /// Splits a text into natural language chunks based on sentence and phrase boundaries.
        /// Tries to respect natural breaks in speech for better synthesis quality.
        /// </summary>
        /// <param name="text">The text to split.</param>
        /// <returns>A list of text chunks.</returns>
        private List<string> SplitTextIntoNaturalChunks(string text)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(text)) return chunks;
            
            // First split by sentence boundaries 
            // Look for end of sentence markers followed by space or end of string
            string[] sentenceSplitters = { ". ", "! ", "? ", ".\n", "!\n", "?\n", ".\r\n", "!\r\n", "?\r\n" };
            
            var currentChunk = new StringBuilder();
            int lastSplitPosition = 0;
            
            for (int i = 0; i < text.Length; i++)
            {
                // Check if we're at a sentence boundary
                bool foundSplitter = false;
                foreach (var splitter in sentenceSplitters)
                {
                    if (i <= text.Length - splitter.Length && 
                        text.Substring(i, splitter.Length) == splitter)
                    {
                        // Include the splitter in the current chunk
                        currentChunk.Append(text.Substring(lastSplitPosition, i - lastSplitPosition + splitter.Length));
                        lastSplitPosition = i + splitter.Length;
                        
                        // If the current chunk is large enough, add it to chunks and reset
                        if (currentChunk.Length >= MinChunkSize)
                        {
                            chunks.Add(currentChunk.ToString().Trim());
                            currentChunk.Clear();
                        }
                        
                        foundSplitter = true;
                        break;
                    }
                }
                
                // Check if we're getting too large without finding a natural break
                if (!foundSplitter && 
                    currentChunk.Length + (i - lastSplitPosition) > MaxChunkSize && 
                    i > lastSplitPosition)
                {
                    // Look for a comma or other phrase boundary to split on
                    int phraseEndPos = FindPhraseEndPosition(text, lastSplitPosition, i);
                    if (phraseEndPos > lastSplitPosition)
                    {
                        // Found a phrase boundary
                        currentChunk.Append(text.Substring(lastSplitPosition, phraseEndPos - lastSplitPosition + 1));
                        lastSplitPosition = phraseEndPos + 1;
                        chunks.Add(currentChunk.ToString().Trim());
                        currentChunk.Clear();
                    }
                    else if (currentChunk.Length > 0)
                    {
                        // Just add what we have so far
                        chunks.Add(currentChunk.ToString().Trim());
                        currentChunk.Clear();
                    }
                    else
                    {
                        // Force a split at a word boundary
                        int splitPos = FindWordBoundary(text, lastSplitPosition + MaxChunkSize / 2);
                        currentChunk.Append(text.Substring(lastSplitPosition, splitPos - lastSplitPosition));
                        lastSplitPosition = splitPos;
                        chunks.Add(currentChunk.ToString().Trim());
                        currentChunk.Clear();
                    }
                }
            }
            
            // Add the remaining text if any
            if (lastSplitPosition < text.Length)
            {
                currentChunk.Append(text.Substring(lastSplitPosition));
            }
            
            if (currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
            }
            
            // Make a final pass to combine chunks that are too small
            List<string> optimizedChunks = new List<string>();
            StringBuilder combined = new StringBuilder();
            
            foreach (var chunk in chunks)
            {
                if (combined.Length + chunk.Length <= MaxChunkSize)
                {
                    if (combined.Length > 0) combined.Append(" ");
                    combined.Append(chunk);
                }
                else
                {
                    if (combined.Length > 0)
                    {
                        optimizedChunks.Add(combined.ToString());
                        combined.Clear();
                    }
                    
                    if (chunk.Length <= MaxChunkSize)
                    {
                        combined.Append(chunk);
                    }
                    else
                    {
                        // This chunk is still too big, add it directly
                        optimizedChunks.Add(chunk);
                    }
                }
            }
            
            if (combined.Length > 0)
            {
                optimizedChunks.Add(combined.ToString());
            }
            
            return optimizedChunks.Count > 0 ? optimizedChunks : chunks;
        }
        
        /// <summary>
        /// Finds a good phrase boundary (comma, semicolon, colon, etc.) within a range of text.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <param name="startIndex">Start position to search from.</param>
        /// <param name="endIndex">End position to search to.</param>
        /// <returns>Position of phrase boundary or -1 if not found.</returns>
        private int FindPhraseEndPosition(string text, int startIndex, int endIndex)
        {
            // Look for phrase separators like comma, semicolon, colon, dash, parentheses
            char[] phraseSeparators = { ',', ';', ':', '-', ')', '(' };
            
            // Search backward from endIndex to find the closest separator
            for (int i = Math.Min(endIndex, text.Length - 1); i >= startIndex; i--)
            {
                if (Array.IndexOf(phraseSeparators, text[i]) >= 0)
                {
                    return i;
                }
            }
            
            return -1; // No suitable separator found
        }
        
        /// <summary>
        /// Finds a word boundary near the given position, to avoid breaking words.
        /// </summary>
        /// <param name="text">The text to search.</param>
        /// <param name="position">The approximate position to find a boundary near.</param>
        /// <returns>Position of a word boundary.</returns>
        private int FindWordBoundary(string text, int position)
        {
            // First look for a space before the position
            for (int i = Math.Min(position, text.Length - 1); i > 0; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return i + 1; // Return the position after the space
                }
            }
            
            // If we can't find a space before, look after
            for (int i = position; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return i;
                }
            }
            
            // If no word boundary found, just return the position
            return Math.Min(position, text.Length);
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