#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Plugins.OpenAI
{    /// <summary>
    /// Recognizer implementation using OpenAI Whisper API.
    /// Automatically switches between HTTP API and Realtime API based on configuration.
    /// Acts as a facade to provide clean unified interface regardless of backend.
    /// </summary>
    public class OpenAIApiRecognizer : IRecognizer, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenAIApiRecognizer> _logger;
        private readonly OpenAIRealtimeRecognizer? _realtimeRecognizer;
        private readonly bool _useRealtimeAPI;

        // Events for streaming results (forward from realtime recognizer)
        public event Func<string, string, bool, Task>? OnTranscriptionReady;
        public event Func<string, Task>? OnSpeechStarted;
        public event Func<string, Task>? OnSpeechEnded;
        public event Func<string, string, Task>? OnError;        public OpenAIApiRecognizer(HttpClient httpClient, ILogger<OpenAIApiRecognizer> logger, string? apiKey = null, bool useRealtimeAPI = false)
        {
            _httpClient = httpClient;
            _logger = logger;
            _useRealtimeAPI = useRealtimeAPI;

            if (_useRealtimeAPI && !string.IsNullOrEmpty(apiKey))
            {
                var realtimeLogger = logger as ILogger<OpenAIRealtimeRecognizer> ?? 
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIRealtimeRecognizer>();
                _realtimeRecognizer = new OpenAIRealtimeRecognizer(apiKey!, realtimeLogger);
                  // Forward events from realtime recognizer
                _realtimeRecognizer.OnTranscriptionReady += async (text, sessionId, isPartial) =>
                {
                    if (OnTranscriptionReady != null)
                        await OnTranscriptionReady(text, sessionId, isPartial);
                };
                _realtimeRecognizer.OnSpeechStarted += async (sessionId) =>
                {
                    if (OnSpeechStarted != null)
                        await OnSpeechStarted(sessionId);
                };
                _realtimeRecognizer.OnSpeechEnded += async (sessionId) =>
                {
                    if (OnSpeechEnded != null)
                        await OnSpeechEnded(sessionId);
                };
                _realtimeRecognizer.OnError += async (sessionId, error) =>
                {
                    if (OnError != null)
                        await OnError(sessionId, error);
                };
                
                _logger.LogInformation("OpenAI Realtime API mode enabled");
            }
            else
            {
                _logger.LogInformation("OpenAI HTTP API mode enabled");
            }
        }

        public async Task<string> RecognizeAsync(Stream audioStream, string language, string? contentType = null, string? fileName = null)
        {
            long dataLength = audioStream.CanSeek ? audioStream.Length : -1;
            // Use a default filename if null or empty, otherwise use the provided filename.
            string effectiveFileName = fileName ?? "audio.wav"; // Simplified null coalescing
            if (string.IsNullOrEmpty(effectiveFileName)) // Ensure it's not empty after coalescing (though ?? "audio.wav" prevents this)
            {
                effectiveFileName = "audio.wav";
            }
            
            // Ensure the filename has an extension, default to .wav if not present or if content type suggests it.
            if (!Path.HasExtension(effectiveFileName) || 
                (contentType == "audio/wav" && Path.GetExtension(effectiveFileName)?.ToLowerInvariant() != ".wav"))
            {
                effectiveFileName = Path.ChangeExtension(effectiveFileName, ".wav");
            }
            // If content type is mp3, ensure extension is .mp3
            else if (contentType == "audio/mpeg" && Path.GetExtension(effectiveFileName)?.ToLowerInvariant() != ".mp3")
            {
                 effectiveFileName = Path.ChangeExtension(effectiveFileName, ".mp3");
            }

            _logger.LogInformation("Whisper API request: model=whisper-1, language={Language}, dataLength={DataLength}, fileName={FileName}, contentType={ContentType}",
                language, dataLength, effectiveFileName, contentType);

            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent("whisper-1"), "model");

            if (!string.IsNullOrEmpty(language))
            {
                multipart.Add(new StringContent(language), "language");
            }

            var streamContent = new StreamContent(audioStream);
            
            // Validate and set ContentType
            string validContentType = "audio/wav"; // Default to audio/wav
            if (!string.IsNullOrEmpty(contentType))
            {
                try
                {
                    // Attempt to parse the provided contentType to ensure it's valid
                    var parsedMediaType = new MediaTypeHeaderValue(contentType);
                    validContentType = parsedMediaType.ToString(); // Use the parsed (and validated) value
                }
                catch (FormatException)
                {
                    _logger.LogWarning("Invalid contentType '{ContentType}' provided. Defaulting to '{DefaultContentType}'. Check the calling code.", contentType, validContentType);
                    // Keep the default "audio/wav" if parsing fails
                }
            }
            streamContent.Headers.ContentType = new MediaTypeHeaderValue(validContentType);
            
            multipart.Add(streamContent, "file", effectiveFileName);

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", multipart);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Whisper API error {StatusCode}: {Body}. Request details: model=whisper-1, language={Language}, fileName={FileName}, contentType={ContentType}", 
                                (int)response.StatusCode, body, language, effectiveFileName, contentType);
                throw new ApplicationException($"Whisper API error {(int)response.StatusCode}: {body}");
            }            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("text", out var textProp))
            {
                _logger.LogError("Whisper API response missing 'text' field: {Body}. Request details: model=whisper-1, language={Language}, fileName={FileName}, contentType={ContentType}", 
                                body, language, effectiveFileName, contentType);
                throw new ApplicationException($"Whisper API response missing 'text' field: {body}");
            }
            var resultText = textProp.GetString() ?? string.Empty;
            _logger.LogInformation("Whisper API response: text=\"{ResponseText}\" for language {Language}, fileName {FileName}", resultText, language, effectiveFileName);
            return resultText;
        }        /// <summary>
        /// Streaming recognition that processes audio chunks as they become available.
        /// This enables faster response times by starting processing before speech ends.
        /// </summary>
        /// <param name="audioChunk">Audio data chunk to process</param>
        /// <param name="language">Language hint for recognition</param>
        /// <param name="isPartial">Whether this is a partial chunk (more audio expected) or final</param>
        /// <returns>Recognized text, may be partial if isPartial=true</returns>
        public async Task<string> RecognizeStreamingAsync(byte[] audioChunk, string language, bool isPartial = true)
        {
            if (audioChunk == null || audioChunk.Length == 0)
            {
                return string.Empty;
            }

            // Use Realtime API if available and enabled
            if (_useRealtimeAPI && _realtimeRecognizer != null)
            {
                return await _realtimeRecognizer.RecognizeStreamingAsync(audioChunk, language, isPartial);
            }

            // Fallback to HTTP API
            try
            {
                using var audioStream = new MemoryStream(audioChunk);
                string fileName = isPartial ? "streaming_chunk.wav" : "final_chunk.wav";
                
                var result = await RecognizeAsync(audioStream, language, "audio/wav", fileName);
                
                _logger.LogDebug("HTTP Streaming recognition: {Length} bytes → \"{Text}\" (partial: {IsPartial})", 
                    audioChunk.Length, result?.Length > 50 ? result.Substring(0, 47) + "..." : result, isPartial);
                
                return result ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HTTP Streaming recognition failed for {Length} bytes: {Error}", audioChunk.Length, ex.Message);
                return string.Empty; // Don't fail the entire pipeline for partial chunks
            }
        }        /// <summary>
        /// Real-time recognition using OpenAI Realtime API.
        /// Falls back to streaming HTTP API if Realtime API is not available.
        /// </summary>
        /// <param name="audioChunk">Audio data chunk</param>
        /// <param name="language">Language hint</param>
        /// <param name="sessionId">Session identifier for tracking</param>
        /// <returns>Real-time recognition result</returns>
        public async Task<string> RecognizeRealtimeAsync(byte[] audioChunk, string language, string sessionId)
        {
            // Use Realtime API if available and enabled
            if (_useRealtimeAPI && _realtimeRecognizer != null)
            {
                return await _realtimeRecognizer.RecognizeRealtimeAsync(audioChunk, language, sessionId);
            }

            // Fallback to streaming HTTP API
            _logger.LogDebug("Realtime API not available, falling back to HTTP streaming mode");
            return await RecognizeStreamingAsync(audioChunk, language, true);
        }

        /// <summary>
        /// Connect to OpenAI Realtime API (if enabled)
        /// </summary>
        public async Task ConnectAsync(string sessionId, string language = "en")
        {
            if (_useRealtimeAPI && _realtimeRecognizer != null)
            {
                await _realtimeRecognizer.ConnectAsync(sessionId, language);
            }
            else
            {
                _logger.LogDebug("Realtime API not enabled, no connection needed");
            }
        }

        /// <summary>
        /// Disconnect from OpenAI Realtime API (if connected)
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_useRealtimeAPI && _realtimeRecognizer != null)
            {
                await _realtimeRecognizer.DisconnectAsync();
            }
        }

        /// <summary>
        /// Check if Realtime API is connected
        /// </summary>
        public bool IsRealtimeConnected => _useRealtimeAPI && _realtimeRecognizer != null;

        public void Dispose()
        {
            _realtimeRecognizer?.Dispose();
        }
    }
}