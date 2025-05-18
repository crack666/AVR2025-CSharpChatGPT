using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VoiceAssistant.Core.Interfaces;
using Xunit;

namespace VoiceAssistant.Tests
{
    public class WebSocketAudioStreamingTests : IClassFixture<WebApplicationFactory<global::Program>>
    {
        private readonly WebApplicationFactory<global::Program> _factory;

        public WebSocketAudioStreamingTests(WebApplicationFactory<global::Program> factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task ElefantenAudio_ReturnsExpectedPrompt_StreamingWebSocket()
        {
            var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Set OPENAI_API_KEY environment variable to run this test.");

            // configure test server to use stub recognizer for fast, deterministic transcription
            var testFactory = _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IRecognizer, StubRecognizer>();
                });
            });
            // connect to websocket audio endpoint on test server
            var wsClient = testFactory.Server.CreateWebSocketClient();
            using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/audio"), CancellationToken.None);
            // verify WebSocket connection is open
            Assert.Equal(WebSocketState.Open, ws.State);

            // load WAV and extract raw PCM frames
            var current = Directory.GetCurrentDirectory();
            var filePath = Path.Combine(current, "TestAudioSamples", "Elefanten.wav");
            Assert.True(File.Exists(filePath), $"Audio file not found: {filePath}");
            var wav = File.ReadAllBytes(filePath);
            const int headerSize = 44;
            const int frameBytes = 16000 * 2 * 20 / 1000;
            var payload = wav.Skip(headerSize).ToArray();
            var totalSize = ((payload.Length + frameBytes - 1) / frameBytes) * frameBytes;
            var buffer = new byte[totalSize];
            Buffer.BlockCopy(payload, 0, buffer, 0, payload.Length);

            // send frames sequentially just like the real Web UI would
            for (int offset = 0; offset < buffer.Length; offset += frameBytes)
            {
                await ws.SendAsync(
                    new ArraySegment<byte>(buffer, offset, frameBytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
            // properly close the WebSocket handshake to signal end of audio
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "End", CancellationToken.None);

            // receive messages until prompt event is found or server closes the socket
            var recvBuffer = new byte[1024];
            string? promptEvent = null;
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(recvBuffer, 0, result.Count);
                    using var doc = JsonDocument.Parse(json);
                    var ev = doc.RootElement.GetProperty("event").GetString();
                    if (ev == "prompt")
                    {
                        promptEvent = doc.RootElement.GetProperty("data").GetProperty("prompt").GetString();
                        break;
                    }
                }
            } while (result.MessageType != WebSocketMessageType.Close);
            Assert.False(string.IsNullOrWhiteSpace(promptEvent));
            Assert.Contains("elefanten", promptEvent!, StringComparison.InvariantCultureIgnoreCase);
            Assert.Contains("wie", promptEvent!, StringComparison.InvariantCultureIgnoreCase);
        }
        
        // stub recognizer returns the expected prompt synchronously
        private class StubRecognizer : IRecognizer
        {
            public Task<string> RecognizeAsync(Stream audioStream, string contentType, string fileName)
            {
                // simulate correct transcript for Elefanten.wav
                return Task.FromResult("Wie groß werden Elefanten?");
            }
        }
    }
}