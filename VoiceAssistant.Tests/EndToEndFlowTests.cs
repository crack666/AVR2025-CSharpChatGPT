using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Plugins.OpenAI;
using Xunit;
using Xunit.Abstractions;

namespace VoiceAssistant.Tests
{
    public class EndToEndFlowTests
    {
        private readonly ITestOutputHelper _output;
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public EndToEndFlowTests(ITestOutputHelper output)
        {
            _output = output;
            _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            if (!string.IsNullOrEmpty(_apiKey))
            {
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                };
                _httpClient = new HttpClient(handler)
                {
                    DefaultRequestVersion = System.Net.HttpVersion.Version20,
                    DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher
                };
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        public async Task Complete_Flow_Should_Work()
        {
            // Skip test if no API key is available
            if (string.IsNullOrEmpty(_apiKey))
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set. Skipping test.");
                return;
            }

            // ARRANGE
            // Setup services
            var chatLogManager = new ChatLogManager();
            var recognizer = new MockRecognizer();
            var chatService = new StreamingOpenAIChatService(_httpClient);
            var synthesizer = new OpenAIApiSynthesizer(_httpClient);

            // ACT
            // 1. Simulate speech recognition
            string userText = "Tell me a joke about programming.";
            _output.WriteLine($"User input: {userText}");

            // 2. Add to chat log
            chatLogManager.AddMessage(ChatRole.User, userText);

            // 3. Get bot response
            string botResponse = await chatService.GenerateResponseAsync(chatLogManager.GetMessages());

            // 4. Add to chat log
            chatLogManager.AddMessage(ChatRole.Bot, botResponse);

            // 5. Generate speech
            byte[] audio = null;
            Exception ttsException = null;

            try
            {
                audio = await synthesizer.SynthesizeAsync(botResponse, "alloy");
            }
            catch (Exception ex)
            {
                ttsException = ex;
                _output.WriteLine($"TTS Exception: {ex}");
            }

            // ASSERT
            // Verify chat log has both messages
            var messages = chatLogManager.GetMessages();
            Assert.Equal(2, messages.Count);
            Assert.Equal(ChatRole.User, messages[0].Role);
            Assert.Equal(userText, messages[0].Content);
            Assert.Equal(ChatRole.Bot, messages[1].Role);

            // Verify bot response is not empty
            Assert.False(string.IsNullOrWhiteSpace(botResponse));
            _output.WriteLine($"Bot response: {botResponse}");

            // Verify TTS generated audio
            Assert.Null(ttsException);
            Assert.NotNull(audio);
            Assert.True(audio.Length > 0);
            _output.WriteLine($"TTS audio size: {audio.Length} bytes");
        }

        [Fact]
        public async Task StreamingChatService_Should_Stream_Responses()
        {
            // Skip test if no API key is available
            if (string.IsNullOrEmpty(_apiKey))
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set. Skipping test.");
                return;
            }

            // ARRANGE
            var chatService = new StreamingOpenAIChatService(_httpClient);
            var chatHistory = new List<ChatMessage>
            {
                new ChatMessage(Guid.NewGuid(), ChatRole.User, "Write one sentence about the weather.", DateTime.UtcNow)
            };

            var tokens = new List<string>();

            // ACT
            string response = await chatService.GenerateStreamingResponseAsync(
                chatHistory,
                token =>
                {
                    tokens.Add(token);
                    _output.WriteLine($"Token: {token}");
                });

            // ASSERT
            Assert.NotEmpty(tokens);
            Assert.NotEmpty(response);
            _output.WriteLine($"Full response: {response}");
            _output.WriteLine($"Token count: {tokens.Count}");

            // Verify all tokens concatenated equal the full response
            Assert.Equal(response, string.Concat(tokens));
        }

        // Simple mock recognizer for testing
        public class MockRecognizer : VoiceAssistant.Core.Interfaces.IRecognizer
        {
            public Task<string> RecognizeAsync(Stream audioStream, string contentType, string fileName)
            {
                // Simply return a mock transcription without actually processing audio
                return Task.FromResult("This is a mock transcription for testing.");
            }
        }
    }
}