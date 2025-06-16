using System.Diagnostics; // Added for Stopwatch
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services; // Added for ChatLogManager
using VoiceAssistant.Plugins.OpenAI; // Assuming StreamingOpenAIChatService is here
using WebRtcVadSharp;

namespace VoiceAssistant
{
    /// <summary>
    /// Service for handling WebSocket-based audio streaming with robust VAD segmentation.
    /// </summary>
    public class WebSocketAudioService
    {
        private PipelineOptions _pipelineOptions; // Made non-readonly
        private readonly IRecognizer _recognizer;
        private readonly IChatService _chatService;
        private readonly ChatLogManager _chatLogManager;
        private readonly ILogger<WebSocketAudioService> _logger;
        private readonly ISynthesizer _synthesizer;
        private WebRtcVad _vad; // Made non-readonly for reconfiguration
        private VadSettings _settings; // Made non-readonly

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;
        private const int FrameDurationMs = 20;
        private readonly int _frameBytes;
        private const int MinSpikeConfirmFrames = 2; // Minimum frames to confirm a spike-primed speech start

        // Noise floor estimation
        private double _noiseFloor;
        private double _silenceDurationSec = 0;

        // TTS voice identifier from pipeline options; no internal storage

        public WebSocketAudioService(
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer,
            ILogger<WebSocketAudioService> logger,
            VadSettings initialSettings, // Renamed for clarity, expects a session-specific instance
            PipelineOptions initialPipelineOptions) // Renamed, expects query params to be pre-applied
        {
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _synthesizer = synthesizer;
            _logger = logger;

            // These are now instance fields that can be modified.
            // It's assumed initialPipelineOptions has already incorporated query parameters.
            _settings = initialSettings;
            _pipelineOptions = initialPipelineOptions;

            _frameBytes = SampleRate * Channels * BitsPerSample / 8 * FrameDurationMs / 1000;

            _vad = new WebRtcVad(); // Initialize _vad here
            ConfigureVad(); // Configure VAD based on initial _settings

            // Initialize noise floor via short calibration window
            _noiseFloor = MeasureInitialNoiseFloor(); // Uses _settings.MinNoiseFloor
            _logger.LogInformation("Initial noise floor: {NoiseFloor:F4}", _noiseFloor);

            // Log initial settings (which should include overrides from query parameters for PipelineOptions)
            LogCurrentSettings();
        }

        private void ConfigureVad()
        {
            if (_vad == null) _vad = new WebRtcVad();
            _vad.OperatingMode = _settings.OperatingMode;
            _vad.SampleRate = WebRtcVadSharp.SampleRate.Is16kHz; // Constant
            _vad.FrameLength = FrameLength.Is20ms;         // Constant

            // VAD related parameters like preFrames, startFrames, endFrames are calculated
            // dynamically in HandleAsync based on the current _settings, so they will adapt.
            _logger.LogInformation("VAD configured with Mode: {Mode}, PreAmp: {PreAmp}, SpikeDetection: {SpikeDetection}, ThirdPartyVad: {ThirdPartyVad}",
                _settings.OperatingMode, _settings.PreAmplification, _settings.EnableSpikeDetection, _settings.EnableThirdPartyVad);
        }

        private void LogCurrentSettings()
        {
            _logger.LogInformation(
                "Current VAD Settings: Mode={Mode}, PreAmp={PreAmp:F1}, MinSpeech={MinSpeech:F2}s, PreSpeech={PreSpeech:F2}s, Hangover={Hangover:F2}s, SpikeDetection={SpikeDetection}, SpikeThreshold={SpikeThreshold}, ThirdPartyVad={ThirdPartyVad}, MinNoiseFloor={MinNoiseFloor}, NoiseFloorAlpha={NoiseFloorAlpha}, NoiseThresholdFactor={NoiseThresholdFactor}, SilenceAdaptationTimeSec={SilenceAdaptationTimeSec}, MinSegmentDurationSec={MinSegmentDurationSec}",
                _settings.OperatingMode, _settings.PreAmplification, _settings.MinSpeechDurationSec,
                _settings.PreSpeechDurationSec, _settings.HangoverDurationSec, _settings.EnableSpikeDetection,
                _settings.VadSpikeThreshold, _settings.EnableThirdPartyVad, _settings.MinNoiseFloor, _settings.NoiseFloorAlpha, _settings.NoiseThresholdFactor, _settings.SilenceAdaptationTimeSec, _settings.MinSegmentDurationSec);

            _logger.LogInformation("Current Pipeline Options: DisableVad={DisableVad}, DisableTts={DisableTts}, DisableProgressiveTts={DisableProgressiveTts}, TtsVoice={TtsVoice}, MinFirstChunk={MinFirst}, MaxFirst={MaxFirst}, SubsequentChunk={Subsequent}, DisableTokenStreaming={DisableTokenStreaming}",
                _pipelineOptions.DisableVad, _pipelineOptions.DisableTts, _pipelineOptions.DisableProgressiveTts, _pipelineOptions.TtsVoice,
                _pipelineOptions.TtsMinFirstChunkLength, _pipelineOptions.TtsMaxFirstChunkLength, _pipelineOptions.TtsSubsequentChunkLength, _pipelineOptions.DisableTokenStreaming);
        }


        private double MeasureInitialNoiseFloor()
        {
            // Implement a short capture of ambient noise (e.g., 1 second) to set MinNoiseFloor
            // For simplicity, use MinNoiseFloor as initial value
            return _settings.MinNoiseFloor;
        }
        public async Task HandleAsync(WebSocket webSocket)
        {
            var sessionId = Guid.NewGuid().ToString("N")[..8]; // Short session ID for correlation
            _logger.LogInformation("Session {SessionId}: WebSocket connected, initializing audio pipeline.", sessionId);
            // Initial settings already logged by constructor after potential query param processing.

            var rawAudio = new List<byte>();
            var audioFrameBuffer = new byte[_frameBytes]; // For binary audio frames from WebSocket
            var messageReceiveBuffer = new byte[8192]; // Buffer for receiving all WebSocket messages

            // VAD parameters, calculated based on current _settings
            // These will be effectively up-to-date if _settings changes, as they are read per loop or re-evaluated.
            int preFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            int startFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);
            int endFrames = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);

            var preBuffer = new Queue<byte[]>();
            var segmentBuffer = new List<byte>();
            bool inSpeech = false;
            int consecSpeech = 0;
            int consecSilence = 0;
            bool potentialSpikeDetected = false;
            bool speechPrimedBySpike = false;

            while (webSocket.State == WebSocketState.Open)
            {
                // Recalculate VAD frame counts in case settings changed via WebSocket message
                preFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
                startFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);
                endFrames = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);

                var receiveSegment = new ArraySegment<byte>(messageReceiveBuffer);
                WebSocketReceiveResult result;
                try
                {
                    result = await webSocket.ReceiveAsync(receiveSegment, CancellationToken.None);
                }
                catch (WebSocketException wsex)
                {
                    _logger.LogError(wsex, "WebSocketException during ReceiveAsync. Ending session.");
                    break;
                }


                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_pipelineOptions.DisableVad)
                    {
                        if (rawAudio.Count > 0)
                        {
                            _logger.LogDebug("Session {SessionId}: VAD disabled, processing all received audio ({Bytes} bytes) on close.", sessionId, rawAudio.Count);
                            await ProcessSegmentAsync(rawAudio.ToArray(), webSocket, sessionId);
                        }
                    }
                    else
                    {
                        if (segmentBuffer.Count > 0)
                        {
                            _logger.LogDebug("Session {SessionId}: WebSocket closing, processing final VAD segment ({Bytes} bytes).", sessionId, segmentBuffer.Count);
                            await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket, sessionId);
                        }
                    }                    // CloseAsync might throw if client already closed abruptly.
                    try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                    catch (WebSocketException ex) { _logger.LogWarning(ex, "WebSocketException during CloseAsync, client might have already disconnected."); }
                    _logger.LogInformation("Session {SessionId}: WebSocket connection closed.", sessionId);
                    break;
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    if (receiveSegment.Array == null)
                    {
                        _logger.LogWarning("Received text message with null buffer. Skipping.");
                        continue;
                    }
                    string messageJson = Encoding.UTF8.GetString(receiveSegment.Array, receiveSegment.Offset, result.Count);
                    _logger.LogDebug("Received text message: {MessageJson}", messageJson);
                    try
                    {
                        using (JsonDocument doc = JsonDocument.Parse(messageJson))
                        {
                            JsonElement root = doc.RootElement;
                            if (root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String)
                            {
                                string? messageType = typeElement.GetString(); // Nullable string
                                if (root.TryGetProperty("payload", out JsonElement payloadElement))
                                {                                    switch (messageType?.ToLowerInvariant())
                                    {
                                        case "updatevadsettings":
                                        case "vad_settings": // Support both naming conventions
                                            await HandleUpdateVadSettingsAsync(payloadElement, webSocket);
                                            break;
                                        case "initialvadsettings": // Handle initial VAD settings
                                            _logger.LogInformation("Processing 'initialVadSettings' message type.");
                                            await HandleUpdateVadSettingsAsync(payloadElement, webSocket);
                                            break;
                                        case "updatepipelineoptions":
                                            await HandleUpdatePipelineOptionsAsync(payloadElement, webSocket);
                                            break;
                                        default:
                                            _logger.LogWarning("Unknown WebSocket message type: {MessageType}", messageType);
                                            break;
                                    }
                                }
                                else { _logger.LogWarning("WebSocket message missing payload: {MessageJson}", messageJson); }
                            }
                            else { _logger.LogWarning("WebSocket message missing type or type is not a string: {MessageJson}", messageJson); }
                        }
                    }
                    catch (JsonException jsonEx) { _logger.LogError(jsonEx, "Error deserializing WebSocket message: {MessageJson}", messageJson); }
                    catch (Exception ex) { _logger.LogError(ex, "Error processing WebSocket message: {MessageJson}", messageJson); }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (result.Count != _frameBytes)
                    {
                        _logger.LogWarning("Received binary message with unexpected size. Expected {ExpectedSize}, got {ActualSize}. Skipping.", _frameBytes, result.Count);
                        continue;
                    }

                    // Copy the received audio frame from messageReceiveBuffer to audioFrameBuffer
                    Array.Copy(messageReceiveBuffer, 0, audioFrameBuffer, 0, _frameBytes);

                    // Copy frame for processing
                    var frame = new byte[_frameBytes];
                    Array.Copy(audioFrameBuffer, frame, _frameBytes); // Use audioFrameBuffer as source
                    rawAudio.AddRange(frame);

                    if (_pipelineOptions.DisableVad)
                    {
                        continue;
                    }

                    // Pre-amplify
                    ApplyPreAmplification(frame);                    // Calculate per-frame RMS
                    double frameRms = CalculateRms(frame);
                    _logger.LogTrace("Session {SessionId}: VAD Frame - RMS={FrameRms:F4}, NoiseFloor={NoiseFloor:F4}, SilenceDur={SilenceDur:F2}s", sessionId, frameRms, _noiseFloor, _silenceDurationSec);

                    // --- BEGIN Spike Detection Logic ---
                    potentialSpikeDetected = false; // Reset at the beginning of each frame processing
                    if (_settings.EnableSpikeDetection)
                    {
                        // A spike is a strong, sudden increase in energy.
                        // It can prime the VAD to start speech even if WebRTC VAD is momentarily negative.                        if (!inSpeech && frameRms > _settings.VadSpikeThreshold && frameRms > _noiseFloor * _settings.NoiseThresholdFactor * 1.5) // Spike must also be significantly above noise floor
                        {
                            potentialSpikeDetected = true;
                            _logger.LogTrace("Session {SessionId}: VAD spike detected - RMS: {FrameRms:F4} > SpikeThreshold: {SpikeThreshold:F4} && RMS > NoiseFloorFactor*1.5: {NoiseFloorFactorThreshold:F4}",
                                                 sessionId, frameRms, _settings.VadSpikeThreshold, _noiseFloor * _settings.NoiseThresholdFactor * 1.5);
                        }
                    }
                    // --- END Spike Detection Logic ---

                    // Run VAD
                    bool hasSpeech = false; // Initialize to false
                    if (_settings.EnableThirdPartyVad) // Check if WebRTC VAD should be used
                    {
                        // Ensure frame is valid for VAD (e.g. correct length for 20ms at 16kHz mono 16-bit)
                        if (frame.Length == _frameBytes)
                        {
                            hasSpeech = _vad.HasSpeech(frame);
                            _logger.LogTrace("Session {SessionId}: VAD Frame - WebRTC VAD HasSpeech={HasSpeech}", sessionId, hasSpeech);
                        }
                        else
                        {
                            _logger.LogWarning("Session {SessionId}: VAD Frame - Invalid frame length for WebRTC VAD. Expected {ExpectedBytes}, Got {ActualBytes}. Assuming no speech.", sessionId, _frameBytes, frame.Length);
                            hasSpeech = false; // Treat as no speech if frame is invalid
                        }
                    }
                    else
                    {                        // If EnableThirdPartyVad is false, we rely solely on RMS/Spike if EnableSpikeDetection is true,
                        // or effectively no VAD if both are off (though DisableVad pipeline option is the main control for that).
                        // For the purpose of `activeSpeechSignal` calculation below, if WebRTC VAD is disabled,
                        // `isWebRtcSpeech` should be considered false or its contribution nullified.
                        _logger.LogTrace("Session {SessionId}: VAD Frame - WebRTC VAD disabled via EnableThirdPartyVad setting.", sessionId);
                    }


                    // Track silence duration
                    if (!hasSpeech)
                        _silenceDurationSec += FrameDurationMs / 1000.0;
                    else
                        _silenceDurationSec = 0;

                    // Update noise floor only after sustained silence
                    if (!hasSpeech && _silenceDurationSec >= _settings.SilenceAdaptationTimeSec)
                    {
                        double oldNoiseFloor = _noiseFloor;
                        _noiseFloor = Math.Max(_settings.MinNoiseFloor,
                            _settings.NoiseFloorAlpha * _noiseFloor + (1 - _settings.NoiseFloorAlpha) * frameRms); if (Math.Abs(oldNoiseFloor - _noiseFloor) > 0.0001) // Log only if changed significantly
                        {
                            _logger.LogDebug("Session {SessionId}: VAD noise floor updated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} after {SilenceDurationSec:F2}s silence (Frame RMS: {FrameRms:F4})",
                                             sessionId, oldNoiseFloor, _noiseFloor, _silenceDurationSec, frameRms);
                        }
                    }

                    // Compute dynamic threshold
                    double dynamicThreshold = Math.Max(_settings.MinNoiseFloor,
                        _noiseFloor * _settings.NoiseThresholdFactor);

                    // Combined decision - incorporating spike detection
                    // A spike can trigger 'isSpeech' even if WebRTC VAD is momentarily negative,
                    // but still require RMS to be above the dynamic threshold to avoid noise spikes.
                    bool isWebRtcSpeech = hasSpeech; // Store original WebRTC VAD result (or result from EnableThirdPartyVad flag)
                    bool isRmsAboveThreshold = frameRms >= dynamicThreshold;
                    _logger.LogTrace("Session {SessionId}: VAD Frame - isWebRtcSpeech={IsWebRtcSpeech}, isRmsAboveThreshold={IsRmsAboveThreshold} (RMS: {FrameRms:F4}, DynThr: {DynamicThreshold:F4})",
                                   sessionId, isWebRtcSpeech, isRmsAboveThreshold, frameRms, dynamicThreshold);

                    // Core speech detection: 
                    // Considers enabled VAD components. If both disabled, activeSpeechSignal will be false.
                    bool activeSpeechSignal = false;
                    if (_settings.EnableThirdPartyVad && _settings.EnableSpikeDetection)
                    {
                        activeSpeechSignal = (isWebRtcSpeech && isRmsAboveThreshold) || (potentialSpikeDetected && isRmsAboveThreshold);
                    }
                    else if (_settings.EnableThirdPartyVad)
                    {
                        activeSpeechSignal = isWebRtcSpeech && isRmsAboveThreshold;
                    }
                    else if (_settings.EnableSpikeDetection)
                    {
                        activeSpeechSignal = potentialSpikeDetected && isRmsAboveThreshold;
                    }

                    // Pre-roll
                    preBuffer.Enqueue(frame);
                    if (preBuffer.Count > preFrames) preBuffer.Dequeue();

                    if (!inSpeech)
                    {
                        // Incorporate potentialSpikeDetected into the start condition
                        if (activeSpeechSignal)
                        {
                            consecSpeech++;
                            if (potentialSpikeDetected) speechPrimedBySpike = true; // Mark that a spike contributed

                            // If primed by spike, require MinSpikeConfirmFrames, otherwise full startFrames
                            bool meetsStartCriteria = (speechPrimedBySpike && consecSpeech >= MinSpikeConfirmFrames) || (!speechPrimedBySpike && consecSpeech >= startFrames);

                            if (meetsStartCriteria)
                            {
                                inSpeech = true;
                                // If started by spike, ensure min speech duration is met by effectively setting consecSpeech high.
                                // Otherwise, use the actual consecutive speech frames.
                                consecSpeech = speechPrimedBySpike ? Math.Max(consecSpeech, MinSpikeConfirmFrames) : consecSpeech;
                                consecSilence = 0;
                                segmentBuffer.Clear();
                                foreach (var buf in preBuffer) segmentBuffer.AddRange(buf);
                                // Add current frame that triggered speech, as preBuffer might not have it if preFrames is 0
                                if (!preBuffer.Contains(frame)) segmentBuffer.AddRange(frame); _logger.LogInformation("Session {SessionId}: VAD speech started (PrimedBySpike: {IsSpikeTriggered}, ConsecSpeechFrames: {ConsecSpeech}, RMS: {FrameRms:F4}, DynThr: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                                     sessionId, speechPrimedBySpike, consecSpeech, frameRms, dynamicThreshold, isWebRtcSpeech);
                                potentialSpikeDetected = false;
                                speechPrimedBySpike = false; // Reset spike priming flag
                            }
                        }
                        else
                        {
                            consecSpeech = 0;
                            potentialSpikeDetected = false; // Reset if no qualifying speech signal follows spike
                            speechPrimedBySpike = false;
                        }
                    }
                    else // inSpeech == true
                    {
                        segmentBuffer.AddRange(frame);
                        // Use activeSpeechSignal for hangover logic as well
                        if (!activeSpeechSignal && ++consecSilence >= endFrames)
                        {
                            inSpeech = false;
                            _logger.LogInformation("Session {SessionId}: VAD speech ended ({Bytes} bytes, ConsecSilenceFrames: {ConsecSilence}, RMS: {FrameRms:F4}, DynThr: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                                 sessionId, segmentBuffer.Count, consecSilence, frameRms, dynamicThreshold, isWebRtcSpeech);
                            await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket, sessionId);
                            segmentBuffer.Clear();
                            consecSpeech = consecSilence = 0;
                            potentialSpikeDetected = false;
                            speechPrimedBySpike = false;
                        }
                        else if (activeSpeechSignal)
                        {
                            consecSilence = 0;
                            // Reset potentialSpikeDetected if speech continues, as it's no longer a "potential" start spike.
                            potentialSpikeDetected = false;
                            speechPrimedBySpike = false;
                        }
                    }
                }
            } // This is line 388, closing the while loop: while (webSocket.State == WebSocketState.Open)
        } // Closing brace for HandleAsync method

        private void ApplyPreAmplification(byte[] frame)
        {
            if (Math.Abs(_settings.PreAmplification - 1.0f) < 0.001f) return; // More robust float comparison
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
            if (count == 0) return 0.0; // Avoid division by zero for empty or malformed frames
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
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true); // leaveOpen is fine
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            // writer.Flush(); // Not strictly necessary here as ToArray() will get the bytes.
            return ms.ToArray();
        }

        // New handler methods for WebSocket messages
        private async Task HandleUpdateVadSettingsAsync(JsonElement payload, WebSocket webSocket)
        {
            _logger.LogInformation("Attempting to update VAD settings from payload: {Payload}", payload.GetRawText());
            try
            {
                // It's better to deserialize into the existing _settings object if possible,
                // or create a new one and assign. For simplicity, creating a new one.
                var updatedSettings = JsonSerializer.Deserialize<VadSettings>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedSettings != null)
                {
                    _settings = updatedSettings;
                    ConfigureVad();

                    double oldNoiseFloor = _noiseFloor;
                    _noiseFloor = MeasureInitialNoiseFloor();
                    if (Math.Abs(oldNoiseFloor - _noiseFloor) > 0.0001)
                    {
                        _logger.LogInformation("Noise floor re-evaluated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} due to VAD settings update.", oldNoiseFloor, _noiseFloor);
                    }

                    _logger.LogInformation("VAD settings updated successfully.");
                    LogCurrentSettings();
                    await SendEventAsync(webSocket, "vad_settings_updated", _settings);
                }
                else { _logger.LogWarning("Failed to deserialize VAD settings payload into a non-null object."); }
            }
            catch (JsonException jsonEx) { _logger.LogError(jsonEx, "Error deserializing VAD settings payload."); }
            catch (Exception ex) { _logger.LogError(ex, "Error updating VAD settings."); }
        }

        private async Task HandleUpdatePipelineOptionsAsync(JsonElement payload, WebSocket webSocket)
        {
            _logger.LogInformation("Attempting to update Pipeline options from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedOptions = JsonSerializer.Deserialize<PipelineOptions>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedOptions != null)
                {
                    _pipelineOptions = updatedOptions;
                    _logger.LogInformation("Pipeline options updated successfully.");
                    LogCurrentSettings();
                    await SendEventAsync(webSocket, "pipeline_options_updated", _pipelineOptions);
                }
                else { _logger.LogWarning("Failed to deserialize Pipeline options payload into a non-null object."); }
            }
            catch (JsonException jsonEx) { _logger.LogError(jsonEx, "Error deserializing Pipeline options payload."); }
            catch (Exception ex) { _logger.LogError(ex, "Error updating Pipeline options."); }
        }        /// <summary>
                 /// Verarbeitet ein erkanntes Audiosegment mit echter End-to-End-Streaming-Pipeline.
                 /// Implementiert paralleles Token-Streaming und TTS-Streaming für minimale Latenz.
                 /// </summary>
                 /// <param name="audioBytes">Rohes Audiosegment (PCM-Daten vom VAD)</param>
                 /// <param name="webSocket">WebSocket-Verbindung für Event-Streaming</param>
                 /// <param name="sessionId">Session ID for log correlation</param>
        private async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket, string sessionId)
        {
            var segmentProcessingStopwatch = Stopwatch.StartNew();
            long transcriptionTimeMs = 0;
            long llmTimeMs = 0;
            string reply = string.Empty; double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            _logger.LogDebug("Session {SessionId}: Processing audio segment - {Bytes} bytes, Duration: {Duration:F3}s", sessionId, audioBytes.Length, durationSec);
            if (durationSec < _settings.MinSegmentDurationSec)
            {
                _logger.LogDebug("Session {SessionId}: Segment discarded - Duration {Duration:F3}s < Min {MinSec:F3}s", sessionId, durationSec, _settings.MinSegmentDurationSec); segmentProcessingStopwatch.Stop();
                // Optionally send an event indicating segment was too short
                // await SendEventAsync(webSocket, "segment_too_short", new { duration = durationSec, minDuration = _settings.MinSegmentDurationSec });
                return;
            }
            try
            {
                _logger.LogDebug("Session {SessionId}: Starting segment processing pipeline", sessionId);

                var transcriptionStopwatch = Stopwatch.StartNew();
                MemoryStream audioMemoryStream = PrepareAudioStreamForTranscription(audioBytes);
                string prompt = await GetTranscriptionAsync(audioMemoryStream);
                transcriptionStopwatch.Stop();
                transcriptionTimeMs = transcriptionStopwatch.ElapsedMilliseconds; if (string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogDebug("Session {SessionId}: Empty transcription, skipping LLM and TTS processing", sessionId);
                    await SendEventAsync(webSocket, "done", new { reason = "Empty transcription, no action taken" });
                    segmentProcessingStopwatch.Stop();
                    _logger.LogDebug("Session {SessionId}: Empty segment processing completed in {TotalTimeMs}ms", sessionId, segmentProcessingStopwatch.ElapsedMilliseconds);
                    return;
                }

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                var llmProcessingStopwatch = Stopwatch.StartNew(); if (!_pipelineOptions.DisableTokenStreaming && _chatService is StreamingOpenAIChatService streamingChatService)
                {
                    reply = await HandleStreamingChatResponseAsync(webSocket, streamingChatService, prompt, _pipelineOptions.ChatModel.ToString(), sessionId); // Pass ChatModel as string
                }
                else // Non-streaming path
                {
                    reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), _pipelineOptions.ChatModel.ToString()); // Pass ChatModel as string
                    _chatLogManager.AddMessage(ChatRole.Bot, reply); // Log bot's reply

                    // Handle non-streaming TTS
                    if (!_pipelineOptions.DisableTts)
                    {
                        _logger.LogDebug("Session {SessionId}: TTS (Non-Progressive) - Synthesizing full reply (Length {Length}): \"{ReplyText}\"", sessionId, reply.Length, reply);
                        var ttsAudioBytes = await _synthesizer.SynthesizeAsync(reply, _pipelineOptions.TtsVoice);
                        if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
                        {
                            await SendAudioChunkAsync(webSocket, ttsAudioBytes, 0, sessionId); // Single chunk
                        }
                        else
                        {
                            _logger.LogWarning("Session {SessionId}: TTS (Non-Progressive) - Synthesizer returned null or empty for reply: \"{ReplyText}\"", sessionId, reply);
                        }
                    }
                }
                llmProcessingStopwatch.Stop();
                llmTimeMs = llmProcessingStopwatch.ElapsedMilliseconds; segmentProcessingStopwatch.Stop();
                long totalProcessingTimeMs = segmentProcessingStopwatch.ElapsedMilliseconds;

                await LogAndSendFinalEventsAsync(webSocket, reply, transcriptionTimeMs, llmTimeMs, totalProcessingTimeMs, sessionId);
            }
            catch (Exception ex)
            {
                segmentProcessingStopwatch.Stop();
                _logger.LogError(ex, "Error processing segment. Total time before error: {TotalTimeMs}ms", segmentProcessingStopwatch.ElapsedMilliseconds);
                await SendEventAsync(webSocket, "error", new { error = ex.Message });
            }
        }

        private MemoryStream PrepareAudioStreamForTranscription(byte[] audioBytes)
        {
            var ms = new MemoryStream();
            var header = CreateWavHeader(audioBytes.Length);
            ms.Write(header, 0, header.Length);
            ms.Write(audioBytes, 0, audioBytes.Length);
            ms.Position = 0;
            return ms;
        }
        private async Task<string> GetTranscriptionAsync(MemoryStream audioMemoryStream)
        {
            string prompt = await _recognizer.RecognizeAsync(audioMemoryStream, _pipelineOptions.Language, "audio/wav", "segment.wav");
            _logger.LogInformation("Transcription complete: '{Prompt}' (Length: {Length})", prompt, prompt.Length);
            return prompt;
        }

        private async Task<string> HandleStreamingChatResponseAsync(WebSocket webSocket, StreamingOpenAIChatService streamingChatService, string prompt, string modelName, string sessionId) // modelName is already string
        {
            var voice = _pipelineOptions.TtsVoice;
            var accumulatedTextForTts = new StringBuilder();
            string fullReply = string.Empty;
            int ttsChunkIndex = 0;
            var sentenceDelimiters = new char[] { '.', '!', '?' };

            await foreach (var (token, isFinalToken) in streamingChatService.StreamTokensAsync(_chatLogManager.GetMessages(), modelName)) // Pass modelName
            {
                if (isFinalToken) break;

                if (!string.IsNullOrEmpty(token))
                {
                    accumulatedTextForTts.Append(token);
                    fullReply += token; // Keep accumulating the full reply for logging and final event
                    await SendEventAsync(webSocket, "token", new { token });
                }

                if (!_pipelineOptions.DisableTts && !_pipelineOptions.DisableProgressiveTts)
                {
                    // Check if the accumulated text contains a sentence boundary
                    // or if it's getting reasonably long and we should try to synthesize.
                    // This is a simplified check; more sophisticated sentence boundary detection could be used.
                    int lastDelimiter = accumulatedTextForTts.ToString().LastIndexOfAny(sentenceDelimiters);

                    // Define a reasonable length to trigger TTS even without a delimiter, to avoid holding too much text.
                    // This can be a new PipelineOption if needed, e.g., TtsMaxBufferBeforeForceFlushChars
                    const int forceFlushLength = 150; // Example value

                    if (lastDelimiter != -1 || accumulatedTextForTts.Length >= forceFlushLength)
                    {
                        string textToSynthesize;
                        if (lastDelimiter != -1)
                        {
                            // Take up to and including the delimiter
                            textToSynthesize = accumulatedTextForTts.ToString().Substring(0, lastDelimiter + 1);
                            accumulatedTextForTts.Remove(0, lastDelimiter + 1);
                        }
                        else // No delimiter, but buffer is long
                        {
                            textToSynthesize = accumulatedTextForTts.ToString();
                            accumulatedTextForTts.Clear();
                        }
                        if (!string.IsNullOrWhiteSpace(textToSynthesize))
                        {
                            _logger.LogDebug("Session {SessionId}: TTS (Progressive) - Sending segment to ChunkedSynthesisAsync (Length {Length}): \"{SegmentText}\"", sessionId, textToSynthesize.Length, textToSynthesize);

                            // ProgressiveTTSSynthesizer will internally split this into smaller audio chunks if needed
                            // and call the onChunkReady callback for each.
                            await _synthesizer.ChunkedSynthesisAsync(textToSynthesize, voice, async (audioBytes) =>
                            {
                                if (audioBytes != null && audioBytes.Length > 0)
                                {
                                    await SendAudioChunkAsync(webSocket, audioBytes, ttsChunkIndex, sessionId);
                                    // Increment ttsChunkIndex here inside the callback to ensure it's unique for each audio piece from ChunkedSynthesisAsync
                                    ttsChunkIndex++;
                                }
                            });
                        }
                    }
                }
            }            // After the loop, synthesize any remaining text in the buffer
            if (!_pipelineOptions.DisableTts && !_pipelineOptions.DisableProgressiveTts && accumulatedTextForTts.Length > 0)
            {
                string finalTextToSynthesize = accumulatedTextForTts.ToString();
                accumulatedTextForTts.Clear();
                _logger.LogDebug("Session {SessionId}: TTS (Progressive) - Sending final accumulated segment to ChunkedSynthesisAsync (Length {Length}): \"{SegmentText}\"", sessionId, finalTextToSynthesize.Length, finalTextToSynthesize);

                await _synthesizer.ChunkedSynthesisAsync(finalTextToSynthesize, voice, async (audioBytes) =>
                {
                    if (audioBytes != null && audioBytes.Length > 0)
                    {
                        await SendAudioChunkAsync(webSocket, audioBytes, ttsChunkIndex, sessionId);
                        ttsChunkIndex++;
                    }
                });
            }

            _chatLogManager.AddMessage(ChatRole.Bot, fullReply);
            return fullReply;
        }

        private async Task<string> HandleNonStreamingChatResponseAsync(WebSocket webSocket, string prompt, string sessionId)
        {            // The prompt is already added to ChatLogManager by ProcessSegmentAsync before this method is called.
            // So, GetMessages() will include the current user prompt.
            string reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), _pipelineOptions.ChatModel.ToString()); // Pass ChatModel as string
            _chatLogManager.AddMessage(ChatRole.Bot, reply); // ChatRole.Bot
            _logger.LogInformation("Session {SessionId}: Non-streaming chat response: '{Reply}'", sessionId, reply);

            if (!_pipelineOptions.DisableTts)
            {
                _logger.LogDebug("Session {SessionId}: TTS (Non-Progressive) - Synthesizing full reply (Length {Length}): \"{ReplyText}\"", sessionId, reply.Length, reply);
                var ttsAudioBytes = await _synthesizer.SynthesizeAsync(reply, _pipelineOptions.TtsVoice); // Returns byte[]
                if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
                {
                    await SendAudioChunkAsync(webSocket, ttsAudioBytes, 0, sessionId); // Pass byte[], Single chunk, index 0
                }
                else { _logger.LogWarning("Session {SessionId}: TTS (Non-Progressive) - Synthesizer returned null or empty stream for reply: \"{ReplyText}\"", sessionId, reply); }
            }
            return reply;
        }

        private async Task LogAndSendFinalEventsAsync(WebSocket webSocket, string reply, long transcriptionTimeMs, long llmTimeMs, long totalTimeMs, string sessionId)
        {
            var latencyInfo = new
            {
                transcriptionTime = transcriptionTimeMs,
                llmTime = llmTimeMs,
                totalTime = totalTimeMs
            };
            await SendEventAsync(webSocket, "reply", new { reply, latency_info = latencyInfo });

            if (!_pipelineOptions.DisableTts)
            {
                // Signal that all audio for this interaction has been sent.
                await SendEventAsync(webSocket, "audio-done", null); // payload can be null
            }
            await SendEventAsync(webSocket, "done", null); // payload can be null
            _logger.LogInformation("Session {SessionId}: Interaction completed - Final reply sent. Latency (ms): Trans={TransTime}, LLM={LlmTime}, Total={TotalTime}",
                sessionId, transcriptionTimeMs, llmTimeMs, totalTimeMs);
        }
        private async Task SendAudioChunkAsync(WebSocket webSocket, byte[] audioBytes, int chunkIndex, string sessionId)
        {
            // byte[] audioBytes was already prepared by the caller or converted if it was a stream.
            // No need to convert from stream to byte array here anymore.

            if (audioBytes.Length == 0)
            {
                _logger.LogWarning("Session {SessionId}: Audio chunk {ChunkIndex} is empty, not sending.", sessionId, chunkIndex);
                return;
            }

            _logger.LogDebug("Session {SessionId}: Sending audio chunk - Index={ChunkIndex}, Size={SizeBytes} bytes", sessionId, chunkIndex, audioBytes.Length);
            // Send audio data as binary message
            await webSocket.SendAsync(new ArraySegment<byte>(audioBytes), WebSocketMessageType.Binary, true, CancellationToken.None);

            // Send metadata as text message
            await SendEventAsync(webSocket, "audio-chunk-info", new { index = chunkIndex, size = audioBytes.Length });
        }

        private async Task SendEventAsync(WebSocket webSocket, string eventName, object? payload) // payload is nullable
        {
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var eventMessage = new { type = eventName, payload = payload };
                    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    var messageJson = JsonSerializer.Serialize(eventMessage, options); var messageBytes = Encoding.UTF8.GetBytes(messageJson);
                    await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);

                    // Log event type at debug level, full payload at trace level for debugging
                    //_logger.LogDebug("Sent event: Type='{EventType}', PayloadJsonLength={PayloadLength}", eventName, messageJson.Length);
                    _logger.LogTrace("Sent event details: Type='{EventType}', Payload='{PayloadJson}'", eventName, messageJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending event: {EventName}", eventName);
                }
            }
        }
    }
}