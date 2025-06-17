using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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
        private readonly Action<string> _onTokenReceived; // Retained for GenerateStreamingResponseAsync if used directly
        private readonly ILogger<StreamingOpenAIChatService> _logger;
        private readonly bool _enableVerboseLogging = false; // Consider making this configurable
        private const string DefaultChatModel = "gpt-4"; // Default model if not specified

        /// <summary>
        /// Initializes a new instance of the <see cref="StreamingOpenAIChatService"/> class.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use for API requests.</param>
        /// <param name="onTokenReceived">Optional callback for real-time token updates (used by GenerateStreamingResponseAsync).</param>
        /// <param name="logger">Optional logger for debugging.</param>
        public StreamingOpenAIChatService(HttpClient httpClient, Action<string> onTokenReceived = null, ILogger<StreamingOpenAIChatService> logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _onTokenReceived = onTokenReceived; // This field might become obsolete or serve a different purpose if GenerateStreamingResponseAsync is removed/refactored
            _logger = logger;
        }

        /// <summary>
        /// Generates a non-streaming response based on the given chat history and chat model.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user + bot).</param>
        /// <param name="chatModel">The OpenAI model to use (e.g., "gpt-4", "gpt-3.5-turbo").</param>
        /// <returns>Generated response text.</returns>
        public async Task<string> GenerateResponseAsync(IEnumerable<ChatMessage> chatHistory, string chatModel)
        {
            if (chatHistory == null)
                throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(chatModel))
                chatModel = DefaultChatModel; // Use default if not provided

            // This method now exclusively handles non-streaming responses.
            return await GenerateNonStreamingResponseAsync(chatHistory, chatModel);
        }

        /// <summary>
        /// Streams chat response tokens asynchronously, fulfilling the IChatService interface.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages.</param>
        /// <param name="chatModel">The OpenAI model to use (e.g., "gpt-4", "gpt-3.5-turbo").</param>
        /// <returns>An asynchronous stream of (token, metadata) tuples.</returns>
        public async IAsyncEnumerable<(string token, bool isFinalToken)> StreamResponseAsync(
            IEnumerable<ChatMessage> chatHistory, 
            string chatModel)
        {
            if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(chatModel)) chatModel = DefaultChatModel;

            var requestPayload = new
            {
                model = chatModel,
                messages = chatHistory.Select(msg => new
                {
                    role = msg.Role == ChatRole.User ? "user" : "assistant",
                    content = msg.Content
                }),
                stream = true
            };

            var stopwatch = Stopwatch.StartNew();
            _logger?.LogInformation("Starting streaming request to OpenAI API with model: {ModelName}", chatModel);

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestPayload), Encoding.UTF8, "application/json")
            };
            // Ensure API key is loaded correctly, e.g., from environment variables or configuration
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger?.LogError("OPENAI_API_KEY environment variable not set.");
                // Consider throwing a specific configuration exception or handling this case gracefully.
                // For now, we'll let it proceed and fail at the API call if the key is indeed missing.
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger?.LogError("OpenAI API request failed with status code {StatusCode}: {ErrorContent}", response.StatusCode, errorContent);
                throw new HttpRequestException($"OpenAI API request failed: {response.StatusCode} - {errorContent}");
            }

            _logger?.LogInformation("Successfully connected to OpenAI API stream. Latency: {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);

            using var responseStream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(responseStream);

            string line;
            bool isFinalChunk = false;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (_enableVerboseLogging) _logger?.LogTrace("Raw SSE line: {Line}", line);

                if (line.StartsWith("data: "))
                {
                    var jsonData = line.Substring("data: ".Length);
                    if (jsonData.Trim().Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation("Stream finished with [DONE] marker.");
                        stopwatch.Stop();
                        _logger?.LogInformation("OpenAI API streaming request completed. Total time: {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
                        // Yield a final empty token if necessary, or ensure the last content token had isFinalToken = true
                        // If the last content token already indicated it was final via finish_reason, this might be redundant.
                        // However, [DONE] is the ultimate confirmation.
                        if (!isFinalChunk) // If no prior chunk was marked as final
                        {
                           // yield return (null, true); // No, we should not yield null token. The last content token should be marked as final.
                        }
                        yield break; 
                    }

                    OpenAIStreamChunk chunk = null;
                    try
                    {
                        chunk = JsonSerializer.Deserialize<OpenAIStreamChunk>(jsonData);
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger?.LogError(jsonEx, "Error deserializing OpenAI stream chunk: {JsonData}", jsonData);
                        continue; 
                    }

                    if (chunk?.Choices != null && chunk.Choices.Any())
                    {
                        var choice = chunk.Choices[0];
                        string currentToken = choice.Delta?.Content;
                        
                        // Determine if this is the final token based on finish_reason
                        isFinalChunk = !string.IsNullOrEmpty(choice.FinishReason);

                        if (!string.IsNullOrEmpty(currentToken))
                        {
                            if (_enableVerboseLogging) _logger?.LogTrace("Received token: {Token}, IsFinal: {IsFinal}", currentToken, isFinalChunk);
                            yield return (currentToken, isFinalChunk);
                        }
                        else if (isFinalChunk)
                        {
                            // If there's a finish_reason but no content in this specific chunk,
                            // it means the stream is ending. The previous token should have been the last content token.
                            // If the previous yield was (someToken, false), this ensures we signal the end.
                            // However, the logic above should correctly set isFinalChunk on the last content-bearing token.
                            // This case might be for scenarios where a final message has no content but a finish_reason.
                            // For now, if currentToken is null/empty, we don't yield, 
                            // relying on [DONE] or the last content token to be marked final.
                             _logger?.LogInformation("Stream segment finished with reason: {FinishReason} but no content in this chunk.", choice.FinishReason);
                        }

                        if (isFinalChunk)
                        {
                             _logger?.LogInformation("Stream segment finished with reason: {FinishReason}", choice.FinishReason);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger?.LogWarning("Received unexpected non-data line from OpenAI stream: {Line}", line);
                }
            }

            stopwatch.Stop();
            _logger?.LogInformation("OpenAI API streaming finished after processing all lines. Total time: {ElapsedMilliseconds}ms", stopwatch.ElapsedMilliseconds);
            // If the loop finishes without a [DONE] marker, it implies an unexpected end of stream.
            // We might need to yield a final marker here if not already done by a finish_reason.
            if (!isFinalChunk) {
                // This case should ideally not be hit if the stream is well-formed and ends with [DONE]
                // or a chunk with a finish_reason.
                _logger?.LogWarning("Stream ended without a [DONE] marker or a final chunk with finish_reason.");
            }
        }

        /// <summary>
        /// Generates a response using the non-streaming (standard) API call.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages.</param>
        /// <param name="modelName">The model to use for generation.</param>
        /// <returns>Complete generated response text.</returns>
        private async Task<string> GenerateNonStreamingResponseAsync(IEnumerable<ChatMessage> chatHistory, string modelName)
        {
            if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(modelName)) modelName = DefaultChatModel; // Ensure modelName is not null or empty

            var requestPayload = new
            {
                model = modelName,
                messages = chatHistory.Select(msg => new
                {
                    role = msg.Role == ChatRole.User ? "user" : "assistant",
                    content = msg.Content
                }),
                stream = false
            };

            var jsonPayload = JsonSerializer.Serialize(requestPayload);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Add Authorization header
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger?.LogError("OPENAI_API_KEY environment variable not set for non-streaming request.");
                // Or throw new InvalidOperationException("OPENAI_API_KEY not configured.");
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

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

    // Helper classes for deserializing OpenAI stream responses
    // These could be moved to a separate file if they grow or are used elsewhere.
    public class OpenAIStreamChunk
    {
        [JsonPropertyName("choices")] // Corrected to lowercase "c"
        public List<Choice> Choices { get; set; }
    }

    public class Choice
    {
        [JsonPropertyName("delta")]
        public Delta Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; }
    }

    public class Delta
    {
        [JsonPropertyName("content")]
        public string Content { get; set; }
    }
}