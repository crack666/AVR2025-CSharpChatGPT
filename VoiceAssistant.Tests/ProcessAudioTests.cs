using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace VoiceAssistant.Tests
{
    public class ProcessAudioTests : IClassFixture<WebApplicationFactory<global::Program>>
    {
        private readonly HttpClient _client;

        public ProcessAudioTests(WebApplicationFactory<global::Program> factory)
        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task ProcessAudio_ReturnsPromptAndResponse()
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Set OPENAI_API_KEY environment variable to run this test.");

            var current = Directory.GetCurrentDirectory();
            var filePath = Path.Combine(current, "TestAudioSamples", "test.wav");
            Assert.True(File.Exists(filePath), $"Audio file not found: {filePath}");

            using var content = new MultipartFormDataContent();
            await using var fs = File.OpenRead(filePath);
            var fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", Path.GetFileName(filePath));
            content.Add(new StringContent("gpt-3.5-turbo"), "model");
            content.Add(new StringContent("en-US"), "language");

            var response = await _client.PostAsync("/api/processAudio", content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"prompt\"", json);
            Assert.Contains("\"response\"", json);
        }
    }
}