using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Recognizer implementation using OpenAI Whisper API.
    /// </summary>
    public class OpenAIApiRecognizer : IRecognizer
    {
        private readonly HttpClient _httpClient;

        public OpenAIApiRecognizer(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> RecognizeAsync(Stream audioStream, string contentType, string fileName)
        {
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
            return textProp.GetString() ?? string.Empty;
        }
    }
}