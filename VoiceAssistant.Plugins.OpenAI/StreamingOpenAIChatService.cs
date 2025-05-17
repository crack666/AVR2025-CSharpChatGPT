using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// Enhanced chat service implementation with true token-by-token streaming.
    /// Provides callbacks for immediate UI updates.
    /// </summary>
    public class StreamingOpenAIChatService : IChatService
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _onTokenReceived;
        private readonly ILogger<StreamingOpenAIChatService> _logger;
        private readonly bool _enableVerboseLogging = true;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingOpenAIChatService"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for API requests.</param>
        /// <param name="onTokenReceived">Optional callback for real-time token updates.</param>
        /// <param name="logger">Optional logger for debugging.</param>
        public StreamingOpenAIChatService(HttpClient httpClient, Action<string> onTokenReceived = null, ILogger<StreamingOpenAIChatService> logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _onTokenReceived = onTokenReceived;
            _logger = logger;
        }
        
        /// <summary>
        /// Generates a response based on the given chat history.
        /// If a token callback is registered, uses streaming mode for real-time updates.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user + bot).</param>
        /// <returns>Generated response text.</returns>
        public async Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory)
        {
            if (chatHistory == null)
                throw new ArgumentNullException(nameof(chatHistory));
                
            // If no token callback is registered, use non-streaming implementation for backward compatibility
            if (_onTokenReceived == null)
            {
                return await GenerateNonStreamingResponseAsync(chatHistory);
            }
            
            // Otherwise use streaming implementation
            return await GenerateStreamingResponseAsync(chatHistory);
        }
        
        /// <summary>
        /// Generates a response using streaming mode with the callback.
        /// This is a public method that can be called directly when token callbacks are needed.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user + bot).</param>
        /// <param name="onTokenReceived">Callback for token-by-token updates.</param>
        /// <returns>Complete generated response text.</returns>
        public async Task<string> GenerateStreamingResponseAsync(
            IEnumerable<ChatMessage> chatHistory, 
            Action<string> onTokenReceived = null)
        {
            if (chatHistory == null)
                throw new ArgumentNullException(nameof(chatHistory));
            
            LogDebug("Starting streaming response generation");
            
            // Check if we have any messages to process
            var messageArray = chatHistory.ToArray();
            if (messageArray.Length == 0)
            {
                LogWarning("Chat history is empty, returning empty response");
                return string.Empty;
            }
            
            // Debug log the chat history
            for (int i = 0; i < messageArray.Length; i++)
            {
                LogDebug($"Chat message {i}: Role={messageArray[i].Role}, Content={messageArray[i].Content?.Substring(0, Math.Min(messageArray[i].Content?.Length ?? 0, 30))}...");
            }
                
            // Use the provided callback or fall back to the constructor-provided one
            var callback = onTokenReceived ?? _onTokenReceived;
            
            // Map internal ChatMessage to OpenAI message format
            var messages = messageArray.Select(msg => new {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            }).ToArray();
            
            LogDebug($"Sending {messages.Length} messages to OpenAI");
            
            var payload = new {
                model = "gpt-3.5-turbo",
                messages = messages,
                stream = true // Enable streaming mode
            };
            
            var jsonPayload = JsonSerializer.Serialize(payload);
            LogDebug($"JSON payload: {jsonPayload}");
            
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            
            // Use a stopwatch to measure response time
            var stopwatch = Stopwatch.StartNew();
            
            // Use ResponseHeadersRead for early processing
            LogDebug("Sending request to OpenAI...");
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            
            LogDebug($"Received headers in {stopwatch.ElapsedMilliseconds}ms, Status: {response.StatusCode}");
            
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                LogError($"HTTP error: {ex.Message}, Response content: {errorContent}");
                throw;
            }
            
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            
            var fullResponse = new StringBuilder();
            string line;
            int tokenCount = 0;
            
            LogDebug("Starting to process streaming response...");
            
            // Process the SSE stream line by line
            while ((line = await reader.ReadLineAsync()) != null)
            {
                // SSE format: "data: {...}" or "data: [DONE]"
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                    {
                        LogDebug("Received [DONE] marker, ending stream processing");
                        break;
                    }
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.GetArrayLength() > 0 &&
                            choices[0].TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content))
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrEmpty(token))
                            {
                                tokenCount++;
                                fullResponse.Append(token);
                                
                                // Invoke the callback if provided
                                callback?.Invoke(token);
                                
                                if (tokenCount % 10 == 0 || _enableVerboseLogging)
                                {
                                    LogDebug($"Received token {tokenCount}: '{token}'");
                                }
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        // Log but continue on JSON parsing errors
                        LogWarning($"JSON parsing error: {ex.Message}, Data: {data}");
                        continue;
                    }
                }
            }
            
            stopwatch.Stop();
            var result = fullResponse.ToString();
            
            LogDebug($"Completed streaming response in {stopwatch.ElapsedMilliseconds}ms, received {tokenCount} tokens");
            LogDebug($"Full response: {result}");
            
            return result;
        }
        
        // Helper methods for logging
        private void LogDebug(string message)
        {
            _logger?.LogDebug(message);
            if (_enableVerboseLogging)
            {
                Console.WriteLine($"[DEBUG] StreamingOpenAIChatService: {message}");
            }
        }
        
        private void LogWarning(string message)
        {
            _logger?.LogWarning(message);
            Console.WriteLine($"[WARNING] StreamingOpenAIChatService: {message}");
        }
        
        private void LogError(string message)
        {
            _logger?.LogError(message);
            Console.WriteLine($"[ERROR] StreamingOpenAIChatService: {message}");
        }
        
        /// <summary>
        /// Generates a response in non-streaming mode (backward compatibility).
        /// </summary>
        /// <param name="chatHistory">The chat history.</param>
        /// <returns>Complete generated response.</returns>
        private async Task<string> GenerateNonStreamingResponseAsync(IEnumerable<ChatMessage> chatHistory)
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