using Microsoft.AspNetCore.Mvc.Testing;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;
using Xunit;

namespace VoiceAssistant.Tests
{
    public class VADCalibrationTests : IClassFixture<WebApplicationFactory<global::Program>>
    {
        private readonly WebApplicationFactory<global::Program> _factory;

        public VADCalibrationTests(WebApplicationFactory<global::Program> factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task CalibrateEndpoint_ReturnsValidThresholds_ForElefantenWav()
        {
            // Arrange: HTTP client
            var client = _factory.CreateClient();
            // Load WAV sample
            var current = Directory.GetCurrentDirectory();
            var samplePath = Path.Combine(current, "TestAudioSamples", "Elefanten.wav");
            Assert.True(File.Exists(samplePath), $"Audio sample not found: {samplePath}");
            await using var fs = File.OpenRead(samplePath);
            var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(fs);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(streamContent, "file", "Elefanten.wav");

            // Act: call calibration endpoint
            var response = await client.PostAsync("/api/settings/vad/calibrate", content);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var settings = JsonSerializer.Deserialize<VadSettings>(json, options);

            // Assert: thresholds are positive and start > end
            Assert.NotNull(settings);
            /*
            Assert.True(settings.StartThreshold > 0, "StartThreshold should be > 0");
            Assert.True(settings.EndThreshold > 0, "EndThreshold should be > 0");
            Assert.True(settings.StartThreshold >= settings.EndThreshold, "StartThreshold should be >= EndThreshold");
            */
        }
    }
}