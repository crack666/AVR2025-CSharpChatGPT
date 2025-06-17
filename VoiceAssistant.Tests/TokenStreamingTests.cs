using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces; // Added for IChatService
using VoiceAssistant.Core.Models;
using VoiceAssistant.Plugins.OpenAI;
using Xunit;
using Xunit.Abstractions;

namespace VoiceAssistant.Tests
{
    public class TokenStreamingTests
    {
        private readonly ITestOutputHelper _output;

        public TokenStreamingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact] // Added Fact attribute to make it discoverable by test runner
        public async Task StreamingOpenAIChatService_Should_Stream_Tokens()
        {
            // Replace with your actual OpenAI API key for testing
            string apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            // Skip test if no API key is available
            if (string.IsNullOrEmpty(apiKey))
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set. Skipping test.");
                return;
            }

            // Arrange
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            var httpClient = new HttpClient(handler)
            {
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher
            };
            // API key is now set within StreamingOpenAIChatService using Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            // httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            IChatService chatService = new StreamingOpenAIChatService(httpClient, logger: null); // Use IChatService

            var chatHistory = new List<ChatMessage>
            {
                new ChatMessage(Guid.NewGuid(), ChatRole.User, "Write a short poem about programming.", DateTime.UtcNow)
            };

            // Collect tokens
            var tokens = new List<string>();
            var fullResponseBuilder = new StringBuilder();
            int tokenCount = 0;
            
            // Act
            // Updated to use StreamResponseAsync and iterate over IAsyncEnumerable
            await foreach (var (token, isFinalToken) in chatService.StreamResponseAsync(chatHistory, "gpt-3.5-turbo"))
            {
                if (!string.IsNullOrEmpty(token))
                {
                    tokens.Add(token);
                    fullResponseBuilder.Append(token);
                    tokenCount++;
                    _output.WriteLine($"Token {tokenCount}: '{token}' (IsFinal: {isFinalToken})");
                }
                if (isFinalToken)
                {
                    _output.WriteLine("Final token received.");
                }
            }
            string fullResponse = fullResponseBuilder.ToString();

            // Assert
            Assert.NotEmpty(tokens);
            Assert.False(string.IsNullOrEmpty(fullResponse));

            // The full response should be the concatenation of all tokens
            string combinedTokens = string.Concat(tokens);
            Assert.Equal(fullResponse, combinedTokens);

            _output.WriteLine($"Full response: {fullResponse}");
            _output.WriteLine($"Received {tokens.Count} tokens");
        }
    }
}