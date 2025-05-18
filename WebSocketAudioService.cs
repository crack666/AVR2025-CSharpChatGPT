using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Plugins.OpenAI;
using WebRtcVadSharp;

namespace VoiceAssistant
{
    /// <summary>
    /// Service for handling WebSocket-based audio streaming with VAD and chat integration.
    /// </summary>
    public class WebSocketAudioService
    {
        private readonly IRecognizer _recognizer;
        private readonly IChatService _chatService;
        private readonly ChatLogManager _chatLogManager;
        private readonly ILogger<WebSocketAudioService> _logger;
        private readonly ISynthesizer _synthesizer;
        private readonly WebRtcVad _vad;

        private const int SampleRate = 16000;
        private const int FrameDurationMs = 20;
        private readonly int _frameBytes;
        private readonly VadSettings _settings;

        // EMA smoothing for RMS
        private readonly double _emaAlpha;
        private float _emaRms;

        // TTS voice identifier provided by client (default: nova)
        private string _ttsVoice = "nova";
        public string TtsVoice
        {
            get => _ttsVoice;
            set => _ttsVoice = value;
        }

        public WebSocketAudioService(
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer,
            ILogger<WebSocketAudioService> logger,
            VadSettings settings)
        {
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _logger = logger;
            _synthesizer = synthesizer;
            _settings = settings;
            // Initialize WebRTC VAD with desired mode, sample rate, and frame length
            _vad = new WebRtcVad
            {
                OperatingMode = WebRtcVadSharp.OperatingMode.Aggressive,
                SampleRate     = WebRtcVadSharp.SampleRate.Is16kHz,
                FrameLength    = WebRtcVadSharp.FrameLength.Is20ms
            };
            _frameBytes = SampleRate * 2 * FrameDurationMs / 1000;
            _emaAlpha = FrameDurationMs / (_settings.RmsSmoothingWindowSec * 1000.0);
            _emaRms = 0;
        }

        public async Task HandleAsync(WebSocket webSocket)
        {
            _logger.LogInformation("WebSocket /ws/audio connected");
            _logger.LogInformation(
                "VAD Settings: StartThreshold={StartThreshold:F4}, EndThreshold={EndThreshold:F4}, RmsSmoothingWindowSec={RmsSmoothingWindowSec:F2}, HangoverDurationSec={HangoverDurationSec:F2}, MinSpeechDurationSec={MinSpeechDurationSec:F2}, PreSpeechDurationSec={PreSpeechDurationSec:F2}",
                _settings.StartThreshold,
                _settings.EndThreshold,
                _settings.RmsSmoothingWindowSec,
                _settings.HangoverDurationSec,
                _settings.MinSpeechDurationSec,
                _settings.PreSpeechDurationSec);
            var segmentBuffer = new List<byte>();
            var buffer = new byte[_frameBytes];
            // Ring buffer to pre-store frames before speech start
            var preSpeechFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            var preBuffer = new Queue<byte[]>();
            // VAD state counters
            var inSpeech = false;
            var speechFrameCount = 0;
            var noSpeechFrames = 0;
            // Minimum frames before starting speech segment
            var minSpeechFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary || result.Count != _frameBytes)
                {
                    _logger.LogInformation("Ignored frame of length {Length} and type {Type}", result.Count, result.MessageType);
                    continue;
                }

                var frame = new byte[_frameBytes];
                Array.Copy(buffer, frame, _frameBytes);
                // maintain pre-speech ring buffer for including a bit of audio before VAD start
                if (preSpeechFrames > 0)
                {
                    preBuffer.Enqueue(frame);
                    if (preBuffer.Count > preSpeechFrames)
                        preBuffer.Dequeue();
                }

                // Compute RMS amplitude and apply amplitude threshold
                var rms = ComputeRms(frame);
                _emaRms = (float)(_emaAlpha * rms + (1 - _emaAlpha) * _emaRms);
                var smoothRms = _emaRms;
                _logger.LogTrace("Smoothed RMS: {SmoothRms:F4}", smoothRms);
                // VAD decision: hysteresis thresholds for start vs. end
                var rmsThreshold = inSpeech ? _settings.EndThreshold : _settings.StartThreshold;
                var passesThreshold = smoothRms >= rmsThreshold;
                var vadSpeech = _vad.HasSpeech(frame);
                var isSpeech = vadSpeech || passesThreshold;

                if (!inSpeech)
                {
                    // Count frames until minimum speech duration is reached
                    if (isSpeech)
                    {
                        speechFrameCount++;
                        if (speechFrameCount >= minSpeechFrames)
                        {
                            inSpeech = true;
                            _logger.LogInformation("VAD: speech start detected");
                            segmentBuffer.Clear();
                            // prepend buffered frames collected before detection
                            if (preSpeechFrames > 0)
                                foreach (var buf in preBuffer)
                                    segmentBuffer.AddRange(buf);
                            noSpeechFrames = 0;
                        }
                    }
                    else
                    {
                        speechFrameCount = 0;
                    }
                }
                else
                {
                    segmentBuffer.AddRange(frame);
                    if (isSpeech)
                    {
                        noSpeechFrames = 0;
                    }
                    else
                    {
                        noSpeechFrames++;
                        var hangoverFrameThreshold = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);
                        if (noSpeechFrames > hangoverFrameThreshold)
                        {
                            inSpeech = false;
                            _logger.LogInformation("VAD: speech end detected, bytes={Bytes}", segmentBuffer.Count);
                            var segmentCopy = segmentBuffer.ToArray();
                            segmentBuffer.Clear();
                            // reset counters for next phrase
                            speechFrameCount = 0;
                            noSpeechFrames = 0;
                            await ProcessSegmentAsync(segmentCopy, webSocket);
                        }
                    }
                }
            }
        }

        private async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket)
        {
            try
            {
                _logger.LogInformation("Processing audio segment of {Bytes} bytes", audioBytes.Length);
                string prompt;
                using var ms = new MemoryStream();
                var header = CreateWavHeader(audioBytes.Length, SampleRate, channels: 1, bitsPerSample: 16);
                ms.Write(header, 0, header.Length);
                ms.Write(audioBytes, 0, audioBytes.Length);
                ms.Position = 0;
                prompt = await _recognizer.RecognizeAsync(ms, "audio/wav", "segment.wav");
                // Log transcription result and its length
                _logger.LogInformation("Transcription prompt: {Prompt} (length {Length})", prompt, prompt.Length);

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                // Generate chat response and stream tokens if supported
                string reply;
                if (_chatService is StreamingOpenAIChatService streaming)
                {
                    reply = await streaming.GenerateStreamingResponseAsync(
                        _chatLogManager.GetMessages(),
                        async token =>
                        {
                            await SendEventAsync(webSocket, "token", new { token });
                        });
                }
                else
                {
                    reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
                    await SendEventAsync(webSocket, "token", new { token = reply });
                }
                _chatLogManager.AddMessage(ChatRole.Bot, reply);
                // Log chat reply
                _logger.LogInformation("Chat reply: {Reply}", reply);

                // Stream TTS audio chunks (base64-encoded) over WebSocket
                try
                {
                    var voice = _ttsVoice;
                    _logger.LogInformation("Using TTS voice: {Voice}", voice);
                    if (_synthesizer is ProgressiveTTSSynthesizer prog)
                    {
                        await prog.ChunkedSynthesisAsync(
                            reply,
                            voice,
                            chunk =>
                            {
                                // Synchronously send each audio chunk
                                SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(chunk) })
                                    .GetAwaiter().GetResult();
                            });
                    }
                    else
                    {
                        var audioBytesOut = await _synthesizer.SynthesizeAsync(reply, voice);
                        await SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(audioBytesOut) });
                    }
                    await SendEventAsync(webSocket, "audio-done", null);
                }
                catch (Exception ttsEx)
                {
                    _logger.LogError(ttsEx, "Error during TTS streaming");
                    await SendEventAsync(webSocket, "error", new { error = ttsEx.Message });
                }

                // Signal completion of this segment
                await SendEventAsync(webSocket, "done", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in audio segment processing");
                await SendEventAsync(webSocket, "error", new { error = ex.Message });
            }
        }

        private static async Task SendEventAsync(WebSocket webSocket, string eventName, object? data)
        {
            var json = data != null
                ? JsonSerializer.Serialize(new { @event = eventName, data })
                : JsonSerializer.Serialize(new { @event = eventName });
            var bytes = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static byte[] CreateWavHeader(int dataLength, int sampleRate, int channels, int bitsPerSample)
        {
            int bytesPerSample = bitsPerSample / 8;
            int byteRate = sampleRate * channels * bytesPerSample;
            short blockAlign = (short)(channels * bytesPerSample);
            using var headerStream = new MemoryStream();
            using var writer = new BinaryWriter(headerStream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Flush();
            return headerStream.ToArray();
        }

    /// <summary>
    /// Computes the RMS amplitude of a 16-bit PCM audio frame.
    /// </summary>
    private static float ComputeRms(byte[] frame)
    {
        int sampleCount = frame.Length / 2;
        double sumSquares = 0;
        for (int i = 0; i < frame.Length; i += 2)
        {
            short sample = BitConverter.ToInt16(frame, i);
            float norm = sample / 32768f;
            sumSquares += norm * norm;
        }
        return (float)Math.Sqrt(sumSquares / sampleCount);
    }
    }
}