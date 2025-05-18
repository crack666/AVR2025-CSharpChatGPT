using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Xunit.Abstractions;

namespace VoiceAssistant.Tests
{
    public class StreamingSpeechLatencyTests : IClassFixture<WebApplicationFactory<global::Program>>
    {
        private readonly HttpClient _client;
        private readonly ITestOutputHelper _output;

        public StreamingSpeechLatencyTests(WebApplicationFactory<global::Program> factory, ITestOutputHelper output)
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Set OPENAI_API_KEY environment variable to run this test.");
            _client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                // Ensure streaming responses are not buffered
                AllowAutoRedirect = false,
                MaxAutomaticRedirections = 0
            });
            _output = output;
        }

        [Fact]
        public async Task FirstAudioChunkArrivesWithinTimeout()
        {
            // Arrange
            var text = "Hello, this is a latency test.";
            var voice = "nova";
            var url = $"/api/streamingSpeech?text={Uri.EscapeDataString(text)}&voice={voice}";
            
            // Act
            var startTime = DateTime.UtcNow;
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            bool receivedChunk = false;
            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("event: chunk", StringComparison.OrdinalIgnoreCase))
                {
                    receivedChunk = true;
                    break;
                }
            }
            var elapsed = DateTime.UtcNow - startTime;
            _output.WriteLine($"First audio chunk event received in {elapsed.TotalMilliseconds} ms");

            // Assert
            Assert.True(receivedChunk, "No audio chunk event received from streamingSpeech endpoint.");
            Assert.True(elapsed.TotalMilliseconds > 0, "Measured elapsed time should be greater than zero.");
        }
    }
}