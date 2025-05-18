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

        public async Task<string> RecognizeAsync(Stream audioStream, string contentType, string fileName)
        {
            // Log request details for debugging
            long dataLength = audioStream.CanSeek ? audioStream.Length : -1;
            _logger.LogInformation("Whisper API request: model=whisper-1, contentType={ContentType}, fileName={FileName}, dataLength={DataLength}",
                contentType, fileName, dataLength);
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent("whisper-1"), "model");
            var fileContent = new StreamContent(audioStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(fileContent, "file", fileName);

            var response = await _httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", multipart);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // Bubble up detailed error for debugging
                throw new ApplicationException($"Whisper API error {(int)response.StatusCode}: {body}");
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("text", out var textProp))
            {
                throw new ApplicationException($"Whisper API response missing 'text' field: {body}");
            }
            var resultText = textProp.GetString() ?? string.Empty;
            _logger.LogInformation("Whisper API response: text={ResponseText}", resultText);
            return resultText;
        }
    }
}