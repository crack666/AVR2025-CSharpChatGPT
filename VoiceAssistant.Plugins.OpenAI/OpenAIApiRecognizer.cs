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
{
    /// <summary>
    /// Recognizer implementation using OpenAI Whisper API.
    /// </summary>
    public class OpenAIApiRecognizer : IRecognizer
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<OpenAIApiRecognizer> _logger;

        public OpenAIApiRecognizer(HttpClient httpClient, ILogger<OpenAIApiRecognizer> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
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
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("text", out var textProp))
            {
                _logger.LogError("Whisper API response missing 'text' field: {Body}. Request details: model=whisper-1, language={Language}, fileName={FileName}, contentType={ContentType}", 
                                body, language, effectiveFileName, contentType);
                throw new ApplicationException($"Whisper API response missing 'text' field: {body}");
            }
            var resultText = textProp.GetString() ?? string.Empty;
            _logger.LogInformation("Whisper API response: text=\"{ResponseText}\" for language {Language}, fileName {FileName}", resultText, language, effectiveFileName);
            return resultText;
        }
    }
}