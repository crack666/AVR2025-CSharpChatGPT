using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Plugins.OpenAI;
using WebRtcVadSharp;

namespace VoiceAssistant
{
    /// <summary>
    /// Service for handling WebSocket-based audio streaming with robust VAD segmentation.
    /// </summary>
    public class WebSocketAudioService
    {
        // Chat model identifier provided by client (default: gpt-3.5-turbo)
        private string _chatModel = "gpt-3.5-turbo";
        public string ChatModel { get => _chatModel; set => _chatModel = value; }
        private readonly PipelineOptions _pipelineOptions;
        private readonly IRecognizer _recognizer;
        private readonly IChatService _chatService;
        private readonly ChatLogManager _chatLogManager;
        private readonly ILogger<WebSocketAudioService> _logger;
        private readonly ISynthesizer _synthesizer;
        private readonly WebRtcVad _vad;
        private readonly VadSettings _settings;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;
        private const int FrameDurationMs = 20;
        private readonly int _frameBytes;

        // Noise floor estimation
        private double _noiseFloor;
        private double _silenceDurationSec = 0;

        private string _ttsVoice = "nova";
        public string TtsVoice { get => _ttsVoice; set => _ttsVoice = value; }

        public WebSocketAudioService(
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer,
            ILogger<WebSocketAudioService> logger,
            VadSettings settings,
            PipelineOptions pipelineOptions)
        {
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _synthesizer = synthesizer;
            _logger = logger;
            _settings = settings;
            _pipelineOptions = pipelineOptions;

            _vad = new WebRtcVad
            {
                OperatingMode = _settings.OperatingMode,
                SampleRate = WebRtcVadSharp.SampleRate.Is16kHz,
                FrameLength = FrameLength.Is20ms
            };

            _frameBytes = SampleRate * Channels * BitsPerSample / 8 * FrameDurationMs / 1000;

            // Initialize noise floor via short calibration window
            _noiseFloor = MeasureInitialNoiseFloor();
        }

        private double MeasureInitialNoiseFloor()
        {
            // Implement a short capture of ambient noise (e.g., 1 second) to set MinNoiseFloor
            // For simplicity, use MinNoiseFloor as initial value
            return _settings.MinNoiseFloor;
        }

        public async Task HandleAsync(WebSocket webSocket)
        {
            _logger.LogInformation("WebSocket /ws/audio connected");
            _logger.LogInformation(
                "VAD Settings: Mode={Mode}, PreAmp={PreAmp:F1}, MinSpeech={MinSpeech:F2}s, PreSpeech={PreSpeech:F2}s, Hangover={Hangover:F2}s",
                _settings.OperatingMode,
                _settings.PreAmplification,
                _settings.MinSpeechDurationSec,
                _settings.PreSpeechDurationSec,
                _settings.HangoverDurationSec);

            var rawAudio = new List<byte>();
            var buffer = new byte[_frameBytes];
            int preFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            int startFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);
            int endFrames = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);

            var preBuffer = new Queue<byte[]>();
            var segmentBuffer = new List<byte>();
            bool inSpeech = false;
            int consecSpeech = 0;
            int consecSilence = 0;

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (segmentBuffer.Count > 0)
                        await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket);
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary || result.Count != _frameBytes)
                    continue;

                // Copy frame
                var frame = new byte[_frameBytes];
                Array.Copy(buffer, frame, _frameBytes);
                rawAudio.AddRange(frame);

                if (_pipelineOptions.DisableVad) continue;

                // Pre-amplify
                ApplyPreAmplification(frame);

                // Calculate per-frame RMS
                double frameRms = CalculateRms(frame);

                // Run VAD
                bool hasSpeech = _vad.HasSpeech(frame);

                // Track silence duration
                if (!hasSpeech)
                    _silenceDurationSec += FrameDurationMs / 1000.0;
                else
                    _silenceDurationSec = 0;

                // Update noise floor only after sustained silence
                if (!hasSpeech && _silenceDurationSec >= _settings.SilenceAdaptationTimeSec)
                {
                    _noiseFloor = Math.Max(_settings.MinNoiseFloor,
                        _settings.NoiseFloorAlpha * _noiseFloor + (1 - _settings.NoiseFloorAlpha) * frameRms);
                }

                // Compute dynamic threshold
                double dynamicThreshold = Math.Max(_settings.MinNoiseFloor,
                    _noiseFloor * _settings.NoiseThresholdFactor);

                // Combined decision
                bool isSpeech = hasSpeech && frameRms >= dynamicThreshold;

                // Pre-roll
                preBuffer.Enqueue(frame);
                if (preBuffer.Count > preFrames)
                    preBuffer.Dequeue();

                if (!inSpeech)
                {
                    if (isSpeech && ++consecSpeech >= startFrames)
                    {
                        inSpeech = true;
                        consecSilence = 0;
                        segmentBuffer.Clear();
                        foreach (var buf in preBuffer) segmentBuffer.AddRange(buf);
                        _logger.LogInformation("VAD: Speech started");
                    }
                    else if (!isSpeech)
                    {
                        consecSpeech = 0;
                    }
                }
                else
                {
                    segmentBuffer.AddRange(frame);
                    if (!isSpeech && ++consecSilence >= endFrames)
                    {
                        inSpeech = false;
                        _logger.LogInformation("VAD: Speech ended ({Bytes} bytes)", segmentBuffer.Count);
                        await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket);
                        segmentBuffer.Clear();
                        consecSpeech = consecSilence = 0;
                    }
                    else if (isSpeech)
                    {
                        consecSilence = 0;
                    }
                }
            }
        }

        private void ApplyPreAmplification(byte[] frame)
        {
            if (_settings.PreAmplification == 1.0f) return;
            for (int i = 0; i < frame.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(frame, i);
                int amplified = (int)(sample * _settings.PreAmplification);
                amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
                var bytes = BitConverter.GetBytes((short)amplified);
                frame[i] = bytes[0];
                frame[i + 1] = bytes[1];
            }
        }

        private static double CalculateRms(byte[] frame)
        {
            double sum = 0;
            int count = frame.Length / 2;
            for (int i = 0; i < frame.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(frame, i);
                sum += sample * sample;
            }
            return Math.Sqrt(sum / count) / short.MaxValue;
        }

        private byte[] CreateWavHeader(int dataLength)
        {
            int byteRate = SampleRate * Channels * BitsPerSample / 8;
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Flush();
            return ms.ToArray();
        }

        private async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket)
        {
            double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            if (durationSec < _settings.MinSegmentDurationSec)
            {
                _logger.LogInformation("Segment verworfen: Dauer {Duration:F3}s < Min {MinSec:F3}s", durationSec, _settings.MinSegmentDurationSec);
                return;
            }
            try
            {
                _logger.LogInformation("Processing segment: {Bytes} bytes", audioBytes.Length);
                using var ms = new MemoryStream();
                var header = CreateWavHeader(audioBytes.Length);
                ms.Write(header, 0, header.Length);
                ms.Write(audioBytes, 0, audioBytes.Length);
                ms.Position = 0;

                string prompt = await _recognizer.RecognizeAsync(ms, "audio/wav", "segment.wav");
                _logger.LogInformation("Transcription: '{Prompt}'", prompt);

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                string reply;
                if (!_pipelineOptions.DisableTokenStreaming && _chatService is StreamingOpenAIChatService)
                {
                    var streaming = (StreamingOpenAIChatService)_chatService;
                    reply = await streaming.GenerateStreamingResponseAsync(
                        _chatLogManager.GetMessages(),
                        async token => await SendEventAsync(webSocket, "token", new { token }));
                }
                else
                {
                    reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
                    await SendEventAsync(webSocket, "token", new { token = reply });
                }
                // Annotate chat log entry with current pipeline settings
                var botMsg = _chatLogManager.AddMessage(
                    ChatRole.Bot,
                    reply,
                    _pipelineOptions.ChatModel,
                    _pipelineOptions.TtsVoice);
                _logger.LogInformation("Reply: '{Reply}'", reply);

                // TTS
                var voice = _ttsVoice;
                _logger.LogInformation("Using TTS voice: {Voice}", voice);
                bool prog = !_pipelineOptions.DisableProgressiveTts && _synthesizer is ProgressiveTTSSynthesizer;
                if (prog)
                {
                    var synth = (ProgressiveTTSSynthesizer)_synthesizer;
                    await synth.ChunkedSynthesisAsync(reply, voice, chunk =>
                        SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(chunk) }).GetAwaiter().GetResult());
                }
                else
                {
                    var audioOut = await _synthesizer.SynthesizeAsync(reply, voice);
                    await SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(audioOut) });
                }
                await SendEventAsync(webSocket, "audio-done", null);
                await SendEventAsync(webSocket, "done", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing segment");
                await SendEventAsync(webSocket, "error", new { error = ex.Message });
            }
        }

        private static async Task SendEventAsync(WebSocket webSocket, string eventName, object data)
        {
            var payload = JsonSerializer.Serialize(new { @event = eventName, data });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}