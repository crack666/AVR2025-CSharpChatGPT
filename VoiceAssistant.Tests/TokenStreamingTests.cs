using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
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

        [Fact(Skip = "This test requires an actual OpenAI API key")]
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
            httpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var chatService = new StreamingOpenAIChatService(httpClient);
            
            var chatHistory = new List<ChatMessage>
            {
                new ChatMessage(Guid.NewGuid(), ChatRole.User, "Write a short poem about programming.", DateTime.UtcNow)
            };

            // Collect tokens
            var tokens = new List<string>();
            int tokenCount = 0;

            // Act
            string fullResponse = await chatService.GenerateStreamingResponseAsync(
                chatHistory,
                token =>
                {
                    tokens.Add(token);
                    tokenCount++;
                    _output.WriteLine($"Token {tokenCount}: '{token}'");
                });

            // Assert
            Assert.NotEmpty(tokens);
            
            // The full response should be the concatenation of all tokens
            string combinedTokens = string.Concat(tokens);
            Assert.Equal(fullResponse, combinedTokens);
            
            _output.WriteLine($"Full response: {fullResponse}");
            _output.WriteLine($"Received {tokens.Count} tokens");
        }
    }
}