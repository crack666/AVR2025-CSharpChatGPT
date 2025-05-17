using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Synthesizer implementation using OpenAI Text-to-Speech API.
    /// </summary>
    public class OpenAIApiSynthesizer : ISynthesizer
    {
        private readonly HttpClient _httpClient;

        public OpenAIApiSynthesizer(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<byte[]> SynthesizeAsync(string text, string voice)
        {
            // Check for empty input to prevent API error
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Text input for speech synthesis cannot be empty.", nameof(text));
            }

            // Trim and ensure minimum valid length
            text = text.Trim();
            if (text.Length == 0)
            {
                text = "No response available.";
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

            var payload = new { model = "tts-1", voice = voice, input = text };
            var body = JsonSerializer.Serialize(payload);
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (!response.IsSuccessStatusCode)
            {
                var msg = System.Text.Encoding.UTF8.GetString(bytes);
                throw new ApplicationException($"TTS failed: {msg}");
            }
            return bytes;
        }
    }
}