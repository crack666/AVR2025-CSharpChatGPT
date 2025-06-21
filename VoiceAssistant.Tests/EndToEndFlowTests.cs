#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text; // Added for StringBuilder
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces; // Added for IChatService
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
        private readonly HttpClient? _httpClient; // Made nullable to reflect potential skip
        private readonly string? _apiKey; // Made nullable

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
                // API key is now set within StreamingOpenAIChatService using Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                // _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        [Fact] // Added [Fact] attribute
        public async Task Complete_Flow_Should_Work()
        {
            // Skip test if no API key is available or httpClient is not initialized
            if (string.IsNullOrEmpty(_apiKey) || _httpClient == null)
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set or HttpClient not initialized. Skipping test.");
                return;
            }

            // ARRANGE
            // Setup services
            var chatLogManager = new ChatLogManager();
            var recognizer = new MockRecognizer(_output); // Pass _output to MockRecognizer
            var chatService = new StreamingOpenAIChatService(_httpClient);
            var synthesizer = new OpenAIApiSynthesizer(_httpClient);

            // ACT
            // 1. Simulate speech recognition
            string userText = "Tell me a joke about programming.";
            _output.WriteLine($"User input: {userText}");

            // 2. Add to chat log
            chatLogManager.AddMessage(ChatRole.User, userText);            // 3. Get bot response
            // Use default model for this test
            string botResponse = await chatService.GenerateResponseAsync(chatLogManager.GetMessages(), "gpt-3.5-turbo");

            // 4. Add to chat log
            chatLogManager.AddMessage(ChatRole.Bot, botResponse);            // 5. Generate speech
            byte[]? audio = null;
            Exception? ttsException = null; try
            {
                // Use default voice for this test
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
            _output.WriteLine($"Bot response: {botResponse}");            // Verify TTS generated audio
            Assert.Null(ttsException);
            Assert.NotNull(audio);
            Assert.True(audio!.Length > 0);
            _output.WriteLine($"TTS audio size: {audio.Length} bytes");
        }

        [Fact]
        public async Task StreamingChatService_Should_Stream_Responses()
        {
            // Skip test if no API key is available or httpClient is not initialized
            if (string.IsNullOrEmpty(_apiKey) || _httpClient == null)
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set or HttpClient not initialized. Skipping test.");
                return;
            }

            // ARRANGE
            IChatService chatService = new StreamingOpenAIChatService(_httpClient); // Use IChatService
            var chatHistory = new List<ChatMessage>
            {
                new ChatMessage(Guid.NewGuid(), ChatRole.User, "Write one sentence about the weather.", DateTime.UtcNow)
            };

            var tokens = new List<string>();
            var fullResponseBuilder = new StringBuilder();
            int tokenCount = 0;

            // ACT
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
            string response = fullResponseBuilder.ToString();

            // ASSERT
            Assert.NotEmpty(tokens);
            Assert.NotEmpty(response);
            _output.WriteLine($"Full response: {response}");
            _output.WriteLine($"Token count: {tokens.Count}");

            // Verify all tokens concatenated equal the full response
            Assert.Equal(response, string.Concat(tokens));
        }        // Simple mock recognizer for testing
        public class MockRecognizer : VoiceAssistant.Core.Interfaces.IRecognizer
        {
            private readonly ITestOutputHelper _mockOutput;

            public MockRecognizer(ITestOutputHelper outputHelper)
            {
                _mockOutput = outputHelper;
            }

            public Task<string> RecognizeAsync(Stream audioStream, string language, string? contentType = null, string? fileName = null)
            {
                // Simply return a mock transcription without actually processing audio
                _mockOutput.WriteLine($"MockRecognizer.RecognizeAsync called with language: {language ?? "not specified"}");
                return Task.FromResult("This is a mock transcription for testing.");
            }

            public Task<string> RecognizeStreamingAsync(byte[] audioChunk, string language, bool isPartial = true)
            {
                _mockOutput.WriteLine($"MockRecognizer.RecognizeStreamingAsync called with language: {language}, isPartial: {isPartial}");
                return Task.FromResult(isPartial ? "This is partial..." : "This is a mock transcription for testing.");
            }

            public Task<string> RecognizeRealtimeAsync(byte[] audioChunk, string language, string sessionId)
            {
                _mockOutput.WriteLine($"MockRecognizer.RecognizeRealtimeAsync called with language: {language}, sessionId: {sessionId}");
                return Task.FromResult("This is a mock transcription for testing.");
            }

            public Task ConnectAsync(string sessionId, string language = "en")
            {
                _mockOutput.WriteLine($"MockRecognizer.ConnectAsync called with sessionId: {sessionId}, language: {language}");
                return Task.CompletedTask;
            }

            public Task DisconnectAsync()
            {
                _mockOutput.WriteLine($"MockRecognizer.DisconnectAsync called");
                return Task.CompletedTask;
            }

            public bool IsRealtimeConnected => false; // Mock: always return false for tests
        }

        [Theory]
        [InlineData("gpt-4", "en", "nova", "en")]
        [InlineData("gpt-3.5-turbo", "es", "onyx", "es")]
        public async Task Complete_Flow_With_Dynamic_Parameters_Should_Work(string chatModel, string chatLanguage, string ttsVoice, string ttsLanguage)
        {
            // Skip test if no API key is available or httpClient is not initialized
            if (string.IsNullOrEmpty(_apiKey) || _httpClient == null)
            {
                _output.WriteLine("OPENAI_API_KEY environment variable not set or HttpClient not initialized. Skipping test.");
                return;
            }

            _output.WriteLine($"Testing with ChatModel: {chatModel}, ChatLanguage: {chatLanguage}, TTSVoice: {ttsVoice}, TTSLanguage: {ttsLanguage}");

            // ARRANGE
            var chatLogManager = new ChatLogManager();
            var recognizer = new MockRecognizer(_output); // Pass _output to MockRecognizer
            var chatService = new StreamingOpenAIChatService(_httpClient);
            var synthesizer = new OpenAIApiSynthesizer(_httpClient);

            // ACT
            // 1. Simulate speech recognition (using mock)
            using var emptyStream = new MemoryStream();
            string userText = await recognizer.RecognizeAsync(emptyStream, language: chatLanguage); // Pass language to mock
            _output.WriteLine($"User input (mocked for language {chatLanguage}): {userText}");

            // 2. Add to chat log
            chatLogManager.AddMessage(ChatRole.User, userText);            // 3. Get bot response with dynamic parameters
            string botResponse = await chatService.GenerateResponseAsync(chatLogManager.GetMessages(), chatModel);

            // 4. Add to chat log
            chatLogManager.AddMessage(ChatRole.Bot, botResponse);            // 5. Generate speech with dynamic parameters
            byte[]? audio = null;
            Exception? ttsException = null; try
            {
                audio = await synthesizer.SynthesizeAsync(botResponse, ttsVoice);
            }
            catch (Exception ex)
            {
                ttsException = ex;
                _output.WriteLine($"TTS Exception: {ex}");
            }            // ASSERT
            var messages = chatLogManager.GetMessages();
            Assert.Equal(2, messages.Count);
            Assert.Equal(ChatRole.User, messages[0].Role);
            Assert.Equal(userText, messages[0].Content);
            Assert.Equal(ChatRole.Bot, messages[1].Role);
            Assert.False(string.IsNullOrWhiteSpace(botResponse));
            _output.WriteLine($"Bot response (model {chatModel}): {botResponse}");
            Assert.Null(ttsException); // Ensure no TTS error
            Assert.NotNull(audio);
            Assert.True(audio!.Length > 0);
            _output.WriteLine($"TTS audio (voice {ttsVoice}, lang {ttsLanguage}) size: {audio.Length} bytes");
        }
    }
}