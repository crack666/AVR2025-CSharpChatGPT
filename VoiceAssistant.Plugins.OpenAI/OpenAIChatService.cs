using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Chat service implementation using OpenAI /chat/completions endpoint.
    /// </summary>
    public class OpenAIChatService : IChatService
    {
        private readonly HttpClient _httpClient;

        public OpenAIChatService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory)
        {
            // Map internal ChatMessage to OpenAI message format
            var messages = chatHistory.Select(msg => new {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            });
            var payload = new {
                model = "gpt-3.5-turbo",
                messages = messages.ToArray(),
                stream = false
            };
            var jsonPayload = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return content ?? string.Empty;
        }
    }
}