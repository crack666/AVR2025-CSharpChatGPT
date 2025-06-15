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
            _onTokenReceived = onTokenReceived;
            _logger = logger;
        }

        /// <summary>
        /// Generates a response based on the given chat history and chat model.
        /// If a token callback is registered, uses streaming mode for real-time updates.
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

            // Use the _onTokenReceived callback to decide the path, but pass DefaultChatModel
            if (_onTokenReceived == null)
            {
                // Non-streaming path
                return await GenerateNonStreamingResponseAsync(chatHistory, chatModel);
            }

            // Streaming path with callback
            return await GenerateStreamingResponseAsync(chatHistory, chatModel, _onTokenReceived);
        }

        /// <summary>
        /// Generates a response using streaming mode with a callback.
        /// This method is kept for compatibility or direct use if a callback pattern is preferred.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages (user + bot).</param>
        /// <param name="modelName">The model to use for generation.</param>
        /// <param name="onTokenReceived">Callback for token-by-token updates.</param>
        /// <returns>Complete generated response text.</returns>
        public async Task<string> GenerateStreamingResponseAsync(
            IEnumerable<ChatMessage> chatHistory,
            string modelName,
            Action<string> onTokenReceived)
        {
            if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(modelName)) throw new ArgumentNullException(nameof(modelName)); // Ensure modelName is not null or empty here as well

            var fullResponse = new StringBuilder();
            await foreach (var (token, _) in StreamTokensAsync(chatHistory, modelName))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    fullResponse.Append(token);
                    onTokenReceived?.Invoke(token);
                }
            }
            return fullResponse.ToString();
        }


        /// <summary>
        /// Streams chat response tokens asynchronously.
        /// This is the primary method for token-by-token streaming.
        /// </summary>
        /// <param name="chatHistory">Ordered list of chat messages.</param>
        /// <param name="modelName">The OpenAI model to use (e.g., "gpt-4", "gpt-3.5-turbo").</param>
        /// <returns>An asynchronous enumerable of (string token, bool isFinalToken) tuples.</returns>
        public async IAsyncEnumerable<(string Token, bool IsFinalToken)> StreamTokensAsync(
            IEnumerable<ChatMessage> chatHistory,
            string modelName)
        {
            if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(modelName)) throw new ArgumentNullException(nameof(modelName)); // Ensure modelName is not null or empty

            LogDebug($"Starting StreamTokensAsync with model: {modelName}");

            var messageArray = chatHistory.ToArray();
            if (messageArray.Length == 0)
            {
                LogWarning("Chat history is empty, yielding no tokens.");
                yield break;
            }

            var messages = messageArray.Select(msg => new
            {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            }).ToArray();

            var payload = new
            {
                model = modelName,
                messages = messages,
                stream = true
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            LogDebug($"JSON payload for StreamTokensAsync: {jsonPayload}");

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var stopwatch = Stopwatch.StartNew();
            LogDebug("Sending request to OpenAI for StreamTokensAsync...");

            HttpResponseMessage response = null; // Initialize to null
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                LogDebug($"Received headers in {stopwatch.ElapsedMilliseconds}ms, Status: {response.StatusCode}");
                response.EnsureSuccessStatusCode(); // Throws on bad status
            }
            catch (HttpRequestException ex)
            {
                string errorContent = "Error response could not be read.";
                if (response != null) // Check if response object exists
                {
                    try 
                    { 
                        errorContent = await response.Content.ReadAsStringAsync(); 
                        ex.Data["ResponseContent"] = errorContent; // Optionally store it in exception data
                    }
                    catch (Exception readEx)
                    {
                        errorContent = $"Failed to read error response content: {readEx.Message}";
                    }
                }
                else
                {
                    errorContent = "HttpResponseMessage object was null.";
                }
                LogError($"HTTP error in StreamTokensAsync: {ex.Message}, Response content: {errorContent}");
                yield break; // Exit if HTTP request itself failed
            }

            // If EnsureSuccessStatusCode passed, proceed to process the stream
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            string line;
            int tokenCount = 0;
            LogDebug("Starting to process streaming response in StreamTokensAsync...");
            bool streamEnded = false;

            while (!streamEnded && (line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("data: "))
                {
                    var data = line.Substring(6);
                    if (data == "[DONE]")
                    {
                        LogDebug("Received [DONE] marker in StreamTokensAsync.");
                        streamEnded = true; // Mark stream as ended
                        yield return (null, true); // Signal completion
                        continue; // Exit loop after processing DONE
                    }

                    // Each line (token) parsing is in its own try-catch
                    string currentToken = null;
                    bool parseError = false;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                            choices.GetArrayLength() > 0 &&
                            choices[0].TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content))
                        {
                            currentToken = content.GetString();
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        LogWarning($"JSON parsing error in StreamTokensAsync: {jsonEx.Message}, Data: {data}");
                        parseError = true;
                        // Optionally yield an error token or specific error object here if needed
                        // yield return ($"ERROR: JSON Parse - {jsonEx.Message}", false); 
                    }

                    if (!parseError && currentToken != null)
                    {
                        tokenCount++;
                        if (tokenCount % 10 == 0 || _enableVerboseLogging)
                        {
                            LogDebug($"Yielding token {tokenCount} in StreamTokensAsync: '{currentToken}'");
                        }
                        yield return (currentToken, false);
                    }
                }
            }

            // If loop finished without [DONE] (e.g. stream cut off), ensure final signal if not already sent
            if (!streamEnded)
            {
                LogWarning("StreamTokensAsync: Loop finished but [DONE] marker was not received. Signaling end.");
                yield return (null, true);
            }

            stopwatch.Stop();
            LogDebug($"Completed StreamTokensAsync in {stopwatch.ElapsedMilliseconds}ms, yielded {tokenCount} tokens.");
        }

        // Helper methods for logging
        private void LogDebug(string message)
        {
            _logger?.LogTrace(message);
            if (_enableVerboseLogging)
            {
                Console.WriteLine($"[TRACE] StreamingOpenAIChatService: {message}");
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
        /// <param name="modelName">The OpenAI model to use.</param>
        /// <returns>Complete generated response.</returns>
        private async Task<string> GenerateNonStreamingResponseAsync(IEnumerable<ChatMessage> chatHistory, string modelName)
        {
            if (chatHistory == null) throw new ArgumentNullException(nameof(chatHistory));
            if (string.IsNullOrEmpty(modelName)) throw new ArgumentNullException(nameof(modelName));

            // Map internal ChatMessage to OpenAI message format
            var messages = chatHistory.Select(msg => new
            {
                role = msg.Role == ChatRole.User ? "user" : "assistant",
                content = msg.Content
            });

            var payload = new
            {
                model = modelName, // Use parameterized model
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