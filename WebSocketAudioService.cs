using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using WebRtcVadSharp;
using VoiceAssistant.Plugins.OpenAI; // Assuming StreamingOpenAIChatService is here
using System.Diagnostics; // Added for Stopwatch
using VoiceAssistant.Core.Services; // Added for ChatLogManager

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

            _logger.LogInformation("Current Pipeline Options: ChatModel={ChatModel}, TtsVoice={TtsVoice}, Language={Language}, DisableVad={DisableVad}, DisableTts={DisableTts}, DisableProgressiveTts={DisableProgressiveTts}, MinFirstChunk={MinFirst}, MaxFirst={MaxFirst}, SubsequentChunk={Subsequent}, DisableTokenStreaming={DisableTokenStreaming}",
                _pipelineOptions.ChatModel, _pipelineOptions.TtsVoice, _pipelineOptions.Language, _pipelineOptions.DisableVad, _pipelineOptions.DisableTts, _pipelineOptions.DisableProgressiveTts,
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
            _logger.LogInformation("WebSocket /ws/audio connected. Initializing session.");

            var rawAudioCollector = new List<byte>(); // Collects all raw audio if VAD is disabled or for debugging
            var audioFrameBuffer = new byte[_frameBytes];
            var messageReceiveBuffer = new byte[8192];

            // Initialize VAD context
            var vadContext = new VadContext
            {
                NoiseFloor = _noiseFloor, // Initial noise floor from MeasureInitialNoiseFloor
                SilenceDurationSec = 0,
                // Other properties default to false/0/empty
            };

            // VAD timing parameters (recalculated if settings change)
            int preFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            int startFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);
            int endFrames = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);
            
            // Local variable for potential spike, as it's per-frame and influences speechPrimedBySpike
            bool currentFramePotentialSpike = false;


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
                        if (rawAudioCollector.Count > 0)
                        {
                            _logger.LogInformation("VAD disabled, processing all received audio ({Bytes} bytes) on close.", rawAudioCollector.Count);
                            await ProcessSegmentAsync(rawAudioCollector.ToArray(), webSocket);
                        }
                    }
                    else
                    {
                        if (vadContext.SegmentBuffer.Count > 0)
                        {
                            _logger.LogInformation("WebSocket closing, processing final VAD segment ({Bytes} bytes).", vadContext.SegmentBuffer.Count);
                            await ProcessSegmentAsync(vadContext.SegmentBuffer.ToArray(), webSocket);
                        }
                    }
                    try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                    catch (WebSocketException ex) { _logger.LogWarning(ex, "WebSocketException during CloseAsync, client might have already disconnected."); }
                    _logger.LogInformation("WebSocket /ws/audio connection closed.");
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
                                {
                                    switch (messageType?.ToLowerInvariant())
                                    {
                                        case "updatevadsettings":
                                            // Pass vadContext to allow updating its NoiseFloor
                                            await HandleUpdateVadSettingsAsync(payloadElement, webSocket, vadContext);
                                            break;
                                        case "initialvadsettings": // Handle initial VAD settings
                                            _logger.LogInformation("Processing 'initialVadSettings' message type.");
                                            await HandleUpdateVadSettingsAsync(payloadElement, webSocket, vadContext);
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
                    var currentFrameBytes = new byte[_frameBytes];
                    Array.Copy(audioFrameBuffer, currentFrameBytes, _frameBytes);
                    
                    rawAudioCollector.AddRange(currentFrameBytes); // Collect all audio

                    if (_pipelineOptions.DisableVad)
                    {
                        continue; // VAD disabled, collect audio and process on close
                    }

                    // Process the frame for VAD
                    ProcessedFrameData frameAnalysisResult = ProcessAudioFrameForVad(currentFrameBytes, vadContext.NoiseFloor, vadContext.SilenceDurationSec);
                    
                    // Update context from analysis
                    vadContext.NoiseFloor = frameAnalysisResult.UpdatedNoiseFloor;
                    vadContext.SilenceDurationSec = frameAnalysisResult.UpdatedSilenceDurationSec;
                    currentFramePotentialSpike = frameAnalysisResult.PotentialSpikeDetectedThisFrame; // Store for UpdateVadState

                    // Update VAD state and process segments if speech ends
                    await UpdateVadStateAndProcessSegmentsAsync(
                        frameAnalysisResult.ActiveSpeechSignal,
                        currentFramePotentialSpike, // Pass the spike status for this frame
                        frameAnalysisResult.AudioFrame, // Pass the (potentially pre-amplified) frame
                        vadContext,
                        webSocket,
                        preFrames, startFrames, endFrames,
                        frameAnalysisResult.Rms, // Pass RMS for logging
                        frameAnalysisResult.IsThirdPartyVadSpeech // Pass WebRTC VAD result for logging
                        );
                }
            }
        }

        private ProcessedFrameData ProcessAudioFrameForVad(byte[] rawFrame, double currentNoiseFloor, double currentSilenceDurationSec)
        {
            var frameForProcessing = new byte[rawFrame.Length];
            Array.Copy(rawFrame, frameForProcessing, rawFrame.Length);

            ApplyPreAmplification(frameForProcessing);
            double frameRms = CalculateRms(frameForProcessing);

            _logger.LogDebug("VAD Frame: RMS={FrameRms:F4}, NoiseFloor={NoiseFloor:F4}, SilenceDur={SilenceDur:F2}s", 
                           frameRms, currentNoiseFloor, currentSilenceDurationSec);

            bool potentialSpike = false;
            if (_settings.EnableSpikeDetection)
            {
                // Spike detection logic (simplified: assumes not in speech for spike priming)
                // The original logic had `!inSpeech` condition. This method is unaware of `inSpeech`.
                // For modularity, we detect a spike based on frame properties.
                // The `UpdateVadState` will use this considering `inSpeech`.
                if (frameRms > _settings.VadSpikeThreshold && frameRms > currentNoiseFloor * _settings.NoiseThresholdFactor * 1.5)
                {
                    potentialSpike = true;
                    _logger.LogInformation("VAD: Potential spike detected this frame. RMS: {FrameRms:F4} > SpikeThreshold: {SpikeThreshold:F4} && RMS > NoiseFloorFactor*1.5: {NoiseFloorFactorThreshold:F4}",
                                         frameRms, _settings.VadSpikeThreshold, currentNoiseFloor * _settings.NoiseThresholdFactor * 1.5);
                }
            }

            bool thirdPartyVadSpeech = false;
            if (_settings.EnableThirdPartyVad)
            {
                if (frameForProcessing.Length == _frameBytes) // _frameBytes is class member
                {
                    thirdPartyVadSpeech = _vad.HasSpeech(frameForProcessing);
                    _logger.LogDebug("VAD Frame: WebRTC VAD HasSpeech={HasSpeech}", thirdPartyVadSpeech);
                }
                else
                {
                    _logger.LogWarning("VAD Frame: Invalid frame length for WebRTC VAD. Expected {ExpectedBytes}, Got {ActualBytes}. Assuming no speech.", _frameBytes, frameForProcessing.Length);
                }
            }
            else
            {
                _logger.LogDebug("VAD Frame: WebRTC VAD disabled via EnableThirdPartyVad setting.");
            }

            double updatedSilenceDuration = currentSilenceDurationSec;
            if (!thirdPartyVadSpeech) // Or use a combined signal if preferred for silence tracking
                updatedSilenceDuration += FrameDurationMs / 1000.0;
            else
                updatedSilenceDuration = 0;

            double updatedNoiseFloor = currentNoiseFloor;
            if (!thirdPartyVadSpeech && updatedSilenceDuration >= _settings.SilenceAdaptationTimeSec)
            {
                updatedNoiseFloor = Math.Max(_settings.MinNoiseFloor,
                    _settings.NoiseFloorAlpha * currentNoiseFloor + (1 - _settings.NoiseFloorAlpha) * frameRms);
                if (Math.Abs(currentNoiseFloor - updatedNoiseFloor) > 0.0001)
                {
                    _logger.LogInformation("VAD: Noise floor updated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} after {SilenceDurationSec:F2}s silence (Frame RMS: {FrameRms:F4})",
                                     currentNoiseFloor, updatedNoiseFloor, updatedSilenceDuration, frameRms);
                }
            }

            double dynamicThreshold = Math.Max(_settings.MinNoiseFloor, updatedNoiseFloor * _settings.NoiseThresholdFactor);
            bool isRmsAboveThreshold = frameRms >= dynamicThreshold;
            _logger.LogDebug("VAD Frame: isWebRtcSpeech={IsWebRtcSpeech}, isRmsAboveThreshold={IsRmsAboveThreshold} (RMS: {FrameRms:F4}, DynThr: {DynamicThreshold:F4})",
                           thirdPartyVadSpeech, isRmsAboveThreshold, frameRms, dynamicThreshold);
            
            bool activeSignal = false;
            if (_settings.EnableThirdPartyVad && _settings.EnableSpikeDetection)
            {
                activeSignal = (thirdPartyVadSpeech && isRmsAboveThreshold) || (potentialSpike && isRmsAboveThreshold);
            }
            else if (_settings.EnableThirdPartyVad)
            {
                activeSignal = thirdPartyVadSpeech && isRmsAboveThreshold;
            }
            else if (_settings.EnableSpikeDetection)
            {
                activeSignal = potentialSpike && isRmsAboveThreshold;
            }
            
            return new ProcessedFrameData(frameForProcessing, frameRms, potentialSpike, thirdPartyVadSpeech, activeSignal, updatedNoiseFloor, updatedSilenceDuration);
        }

        private async Task UpdateVadStateAndProcessSegmentsAsync(
            bool activeSpeechSignalThisFrame, 
            bool potentialSpikeDetectedThisFrame,
            byte[] processedFrame,
            VadContext vadContext, 
            WebSocket webSocket,
            int preFrames, int startFrames, int endFrames,
            double frameRms, bool isThirdPartyVadSpeech // For logging
            )
        {
            vadContext.PreBuffer.Enqueue(processedFrame);
            if (vadContext.PreBuffer.Count > preFrames) vadContext.PreBuffer.Dequeue();

            if (!vadContext.InSpeech)
            {
                if (activeSpeechSignalThisFrame)
                {
                    vadContext.ConsecSpeech++;
                    if (potentialSpikeDetectedThisFrame) // A spike in this frame contributes to priming
                    {
                        vadContext.SpeechPrimedBySpike = true;
                    }

                    bool meetsStartCriteria = (vadContext.SpeechPrimedBySpike && vadContext.ConsecSpeech >= MinSpikeConfirmFrames) || 
                                              (!vadContext.SpeechPrimedBySpike && vadContext.ConsecSpeech >= startFrames);

                    if (meetsStartCriteria)
                    {
                        vadContext.InSpeech = true;
                        vadContext.ConsecSpeech = vadContext.SpeechPrimedBySpike ? Math.Max(vadContext.ConsecSpeech, MinSpikeConfirmFrames) : vadContext.ConsecSpeech;
                        vadContext.ConsecSilence = 0;
                        vadContext.SegmentBuffer.Clear();
                        foreach (var buf in vadContext.PreBuffer) vadContext.SegmentBuffer.AddRange(buf);
                        // The current 'processedFrame' is already in PreBuffer if preFrames > 0. 
                        // If preFrames is 0, PreBuffer might be empty or not contain current frame.
                        // The original logic added current frame if not in preBuffer.
                        // Enqueueing first ensures it's there if preFrames allows.
                        // If preFrames = 0, preBuffer.Enqueue(frame) then preBuffer.Dequeue() means it's empty.
                        // Let's ensure the current frame is added if not captured by preBuffer logic.
                        // The original check was: if (!preBuffer.Contains(frame)) segmentBuffer.AddRange(frame);
                        // If preFrames == 0, the segmentBuffer is empty after the loop, so we must add the current frame.
                        if (preFrames == 0)
                        {
                            vadContext.SegmentBuffer.AddRange(processedFrame);
                        }


                        _logger.LogInformation("VAD: Speech started (PrimedBySpike: {IsSpikeTriggered}, ConsecSpeechFrames: {ConsecSpeech}, RMS: {FrameRms:F4}, DynThrRelevantToFrame: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                             vadContext.SpeechPrimedBySpike, vadContext.ConsecSpeech, frameRms, 
                                             Math.Max(_settings.MinNoiseFloor, vadContext.NoiseFloor * _settings.NoiseThresholdFactor), // Recalculate for log
                                             isThirdPartyVadSpeech);
                        
                        // Reset spike priming flag once speech has officially started
                        vadContext.SpeechPrimedBySpike = false; 
                    }
                }
                else // No active speech signal
                {
                    vadContext.ConsecSpeech = 0;
                    vadContext.SpeechPrimedBySpike = false; // Reset if no qualifying speech signal follows spike
                }
            }
            else // vadContext.InSpeech == true
            {
                vadContext.SegmentBuffer.AddRange(processedFrame);
                if (!activeSpeechSignalThisFrame)
                {
                    vadContext.ConsecSilence++;
                    if (vadContext.ConsecSilence >= endFrames)
                    {
                        vadContext.InSpeech = false;
                        _logger.LogInformation("VAD: Speech ended ({Bytes} bytes, ConsecSilenceFrames: {ConsecSilence}, RMS: {FrameRms:F4}, DynThrRelevantToFrame: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                             vadContext.SegmentBuffer.Count, vadContext.ConsecSilence, frameRms,
                                             Math.Max(_settings.MinNoiseFloor, vadContext.NoiseFloor * _settings.NoiseThresholdFactor), // Recalculate for log
                                             isThirdPartyVadSpeech);
                        
                        await ProcessSegmentAsync(vadContext.SegmentBuffer.ToArray(), webSocket);
                        vadContext.SegmentBuffer.Clear();
                        vadContext.ConsecSpeech = 0;
                        vadContext.ConsecSilence = 0;
                        vadContext.SpeechPrimedBySpike = false; // Reset for next segment
                    }
                }
                else // Active speech signal continues
                {
                    vadContext.ConsecSilence = 0;
                    vadContext.SpeechPrimedBySpike = false; // If speech continues, any prior priming is resolved.
                }
            }
        }

        private void ApplyPreAmplification(byte[] frame)
        {
            if (Math.Abs(_settings.PreAmplification - 1.0f) < 0.001f) return; 
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
            if (count == 0) return 0.0; 
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
            writer.Write((short)1); // PCM
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            return ms.ToArray();
        }

        // Modify HandleUpdateVadSettingsAsync to accept and update VadContext
        private async Task HandleUpdateVadSettingsAsync(JsonElement payload, WebSocket webSocket, VadContext vadContextToUpdate)
        {
            _logger.LogInformation("Attempting to update VAD settings from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedSettings = JsonSerializer.Deserialize<VadSettings>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedSettings != null)
                {
                    _settings = updatedSettings; // Update the class's _settings field
                    ConfigureVad(); // Reconfigure _vad instance

                    // Re-evaluate noise floor based on new settings and update the context
                    // This mirrors the logic from the constructor and initial setup.
                    double oldNoiseFloor = vadContextToUpdate.NoiseFloor;
                    vadContextToUpdate.NoiseFloor = MeasureInitialNoiseFloor(); // Uses new _settings.MinNoiseFloor
                    
                    if (Math.Abs(oldNoiseFloor - vadContextToUpdate.NoiseFloor) > 0.0001)
                    {
                        _logger.LogInformation("Noise floor re-evaluated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} due to VAD settings update.", oldNoiseFloor, vadContextToUpdate.NoiseFloor);
                    }
                    // Reset silence duration as settings changed, affecting thresholds
                    vadContextToUpdate.SilenceDurationSec = 0;


                    _logger.LogInformation("VAD settings updated successfully.");
                    LogCurrentSettings(); // Logs the new _settings and _pipelineOptions
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
        }

        /// <summary>
        /// Verarbeitet ein erkanntes Audiosegment mit echter End-to-End-Streaming-Pipeline.
        /// Implementiert paralleles Token-Streaming und TTS-Streaming für minimale Latenz.
        /// </summary>
        /// <param name="audioBytes">Rohes Audiosegment (PCM-Daten vom VAD)</param>
        /// <param name="webSocket">WebSocket-Verbindung für Event-Streaming</param>
        private async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket)
        {
            var segmentProcessingStopwatch = Stopwatch.StartNew();
            long transcriptionTimeMs = 0;
            long llmTimeMs = 0;
            string reply = string.Empty;

            double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            _logger.LogInformation("ProcessSegmentAsync: Received {Bytes} bytes, Duration: {Duration:F3}s", audioBytes.Length, durationSec);
            if (durationSec < _settings.MinSegmentDurationSec)
            {
                _logger.LogWarning("Segment verworfen: Dauer {Duration:F3}s < Min {MinSec:F3}s", durationSec, _settings.MinSegmentDurationSec);
                segmentProcessingStopwatch.Stop();
                // Optionally send an event indicating segment was too short
                // await SendEventAsync(webSocket, "segment_too_short", new { duration = durationSec, minDuration = _settings.MinSegmentDurationSec });
                return;
            }
            try
            {
                _logger.LogInformation("Processing segment: {Bytes} bytes", audioBytes.Length);

                var transcriptionStopwatch = Stopwatch.StartNew();
                MemoryStream audioMemoryStream = PrepareAudioStreamForTranscription(audioBytes);
                string prompt = await GetTranscriptionAsync(audioMemoryStream);
                transcriptionStopwatch.Stop();
                transcriptionTimeMs = transcriptionStopwatch.ElapsedMilliseconds;

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogInformation("Transcription resulted in an empty or whitespace prompt. Skipping LLM call and TTS.");
                    await SendEventAsync(webSocket, "done", new { reason = "Empty transcription, no action taken" });
                    segmentProcessingStopwatch.Stop();
                    _logger.LogInformation("Empty segment processing took {TotalTimeMs}ms", segmentProcessingStopwatch.ElapsedMilliseconds);
                    return;
                }

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                var llmProcessingStopwatch = Stopwatch.StartNew();
                if (!_pipelineOptions.DisableTokenStreaming && _chatService is StreamingOpenAIChatService streamingChatService)
                {
                    reply = await HandleStreamingChatResponseAsync(webSocket, streamingChatService, prompt, _pipelineOptions.ChatModel);
                }
                else // Non-streaming path
                {
                    reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), _pipelineOptions.ChatModel);
                    _chatLogManager.AddMessage(ChatRole.Bot, reply); // Log bot's reply

                    // Handle non-streaming TTS
                    if (!_pipelineOptions.DisableTts)
                    {
                        _logger.LogInformation("TTS (Non-Progressive): Synthesizing full reply for non-streaming chat (Length {Length}): \"{ReplyText}...\"", reply.Length, reply.Substring(0, Math.Min(50, reply.Length)));
                        var ttsAudioBytes = await _synthesizer.SynthesizeAsync(reply, _pipelineOptions.TtsVoice);
                        if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
                        {
                            await SendAudioChunkAsync(webSocket, ttsAudioBytes, 0); // Single chunk
                        }
                        else
                        {
                            _logger.LogWarning("TTS (Non-Progressive): Synthesizer returned null or empty for reply: \"{ReplyText}\"", reply);
                        }
                    }
                }
                llmProcessingStopwatch.Stop();
                llmTimeMs = llmProcessingStopwatch.ElapsedMilliseconds;

                segmentProcessingStopwatch.Stop();
                long totalProcessingTimeMs = segmentProcessingStopwatch.ElapsedMilliseconds;

                await LogAndSendFinalEventsAsync(webSocket, reply, transcriptionTimeMs, llmTimeMs, totalProcessingTimeMs);
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
            // Correctly pass parameters: audioStream, language, contentType, fileName
            string languageCode = _pipelineOptions.Language; // e.g., "de-DE"
            // Whisper API expects ISO-639-1 (e.g., "de"), so we might need to extract it.
            if (!string.IsNullOrEmpty(languageCode) && languageCode.Contains("-"))
            {
                languageCode = languageCode.Split('-')[0]; // Get "de" from "de-DE"
            }

            string effectiveFileName = !string.IsNullOrEmpty(_pipelineOptions.Language) ? $"{_pipelineOptions.Language}.wav" : "audio.wav";

            string prompt = await _recognizer.RecognizeAsync(
                audioStream: audioMemoryStream, 
                language: languageCode,                  // Pass the extracted ISO-639-1 language code
                contentType: "audio/wav",              // Explicitly set content type
                fileName: effectiveFileName             // Construct a filename
            );
            _logger.LogInformation("Transcription: '{Prompt}' (Length: {Length}), Language from options: {LanguageOption}, Sent to API: {LanguageApi}", prompt, prompt.Length, _pipelineOptions.Language, languageCode);
            return prompt;
        }

        private async Task<string> HandleStreamingChatResponseAsync(WebSocket webSocket, StreamingOpenAIChatService streamingChatService, string prompt, string modelName)
        {
            var voice = _pipelineOptions.TtsVoice; // Ensure TtsVoice is used from _pipelineOptions
            var accumulatedTextForTts = new StringBuilder();
            string fullReply = string.Empty;
            int ttsChunkIndex = 0;
            var sentenceDelimiters = new char[] { '.', '!', '?' };

            await foreach (var (token, isFinalToken) in streamingChatService.StreamTokensAsync(_chatLogManager.GetMessages(), modelName))
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
                            _logger.LogInformation("TTS (Progressive): Sending segment to ChunkedSynthesisAsync (Length {Length}): \"{SegmentText}...\"", textToSynthesize.Length, textToSynthesize.Substring(0, Math.Min(50, textToSynthesize.Length)));
                            
                            // ProgressiveTTSSynthesizer will internally split this into smaller audio chunks if needed
                            // and call the onChunkReady callback for each.
                            await _synthesizer.ChunkedSynthesisAsync(textToSynthesize, voice, async (audioBytes) => {
                                if (audioBytes != null && audioBytes.Length > 0)
                                {
                                    await SendAudioChunkAsync(webSocket, audioBytes, ttsChunkIndex);
                                    // Increment ttsChunkIndex here inside the callback to ensure it's unique for each audio piece from ChunkedSynthesisAsync
                                    ttsChunkIndex++; 
                                }
                            });
                        }
                    }
                }
            }

            // After the loop, synthesize any remaining text in the buffer
            if (!_pipelineOptions.DisableTts && !_pipelineOptions.DisableProgressiveTts && accumulatedTextForTts.Length > 0)
            {
                string finalTextToSynthesize = accumulatedTextForTts.ToString();
                accumulatedTextForTts.Clear();
                _logger.LogInformation("TTS (Progressive): Sending final accumulated segment to ChunkedSynthesisAsync (Length {Length}): \"{SegmentText}...\"", finalTextToSynthesize.Length, finalTextToSynthesize.Substring(0, Math.Min(50, finalTextToSynthesize.Length)));
                
                await _synthesizer.ChunkedSynthesisAsync(finalTextToSynthesize, voice, async (audioBytes) => {
                     if (audioBytes != null && audioBytes.Length > 0)
                     {
                        await SendAudioChunkAsync(webSocket, audioBytes, ttsChunkIndex);
                        ttsChunkIndex++;
                     }
                });
            }
            
            _chatLogManager.AddMessage(ChatRole.Bot, fullReply);
            return fullReply;
        }

        private async Task<string> HandleNonStreamingChatResponseAsync(WebSocket webSocket, string prompt)
        {
            // The prompt is already added to ChatLogManager by ProcessSegmentAsync before this method is called.
            // So, GetMessages() will include the current user prompt.
            string reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages(), _pipelineOptions.ChatModel); // Use _pipelineOptions.ChatModel
            _chatLogManager.AddMessage(ChatRole.Bot, reply); // ChatRole.Bot
            _logger.LogInformation("Non-streaming chat response: '{Reply}'", reply); // Corrected logging format

            if (!_pipelineOptions.DisableTts)
            {
                _logger.LogInformation("TTS (Non-Progressive): Synthesizing full reply (Length {Length}): \"{ReplyText}...\"", reply.Length, reply.Substring(0, Math.Min(50, reply.Length)));
                var ttsAudioBytes = await _synthesizer.SynthesizeAsync(reply, _pipelineOptions.TtsVoice); // Use _pipelineOptions.TtsVoice
                if (ttsAudioBytes != null && ttsAudioBytes.Length > 0)
                {
                    await SendAudioChunkAsync(webSocket, ttsAudioBytes, 0); // Pass byte[], Single chunk, index 0
                }
                 else { _logger.LogWarning("TTS (Non-Progressive): Synthesizer returned null or empty stream for reply: \"{ReplyText}\"", reply); }
            }
            return reply;
        }

        private async Task LogAndSendFinalEventsAsync(WebSocket webSocket, string reply, long transcriptionTimeMs, long llmTimeMs, long totalTimeMs)
        {
            var latencyInfo = new {
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
            _logger.LogInformation("Interaction completed. Final reply sent. Latency (ms): Trans={TransTime}, LLM={LlmTime}, Total={TotalTime}",
                transcriptionTimeMs, llmTimeMs, totalTimeMs);
        }

        private async Task SendAudioChunkAsync(WebSocket webSocket, byte[] audioBytes, int chunkIndex) // Changed Stream to byte[]
        {
            // byte[] audioBytes was already prepared by the caller or converted if it was a stream.
            // No need to convert from stream to byte array here anymore.

            if (audioBytes.Length == 0)
            {
                _logger.LogWarning("SendAudioChunkAsync: Audio chunk {ChunkIndex} is empty, not sending.", chunkIndex);
                return;
            }

            _logger.LogInformation("Sending audio chunk: Index={ChunkIndex}, Size={SizeBytes} bytes", chunkIndex, audioBytes.Length);
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
                    var messageJson = JsonSerializer.Serialize(eventMessage, options);
                    var messageBytes = Encoding.UTF8.GetBytes(messageJson);
                    await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogInformation("Sent event: Type='{EventType}', PayloadJsonLength={PayloadLength}", eventName, messageJson.Length);
                    // Avoid logging full payload unless at Trace or very specific Debug, can be verbose.
                    // _logger.LogDebug("Sent event details: Type='{EventType}', Payload='{PayloadJson}'", eventName, messageJson); 
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending event: {EventName}", eventName);
                }
            }
        }

        // Helper class to hold VAD state
        private class VadContext
        {
            public bool InSpeech { get; set; }
            public int ConsecSpeech { get; set; }
            public int ConsecSilence { get; set; }
            public bool SpeechPrimedBySpike { get; set; }
            public double NoiseFloor { get; set; }
            public double SilenceDurationSec { get; set; }
            public Queue<byte[]> PreBuffer { get; } = new Queue<byte[]>();
            public List<byte> SegmentBuffer { get; } = new List<byte>();
            // PotentialSpikeDetected is more of a per-frame result, will be handled by ProcessedFrameData
        }

        // Helper struct for per-frame VAD analysis results
        private readonly struct ProcessedFrameData
        {
            public byte[] AudioFrame { get; } // The raw or pre-amplified frame
            public double Rms { get; }
            public bool PotentialSpikeDetectedThisFrame { get; }
            public bool IsThirdPartyVadSpeech { get; }
            public bool ActiveSpeechSignal { get; }
            public double UpdatedNoiseFloor { get; }
            public double UpdatedSilenceDurationSec { get; }

            public ProcessedFrameData(byte[] audioFrame, double rms, bool potentialSpike, bool thirdPartyVadSpeech, bool activeSignal, double noiseFloor, double silenceDuration)
            {
                AudioFrame = audioFrame;
                Rms = rms;
                PotentialSpikeDetectedThisFrame = potentialSpike;
                IsThirdPartyVadSpeech = thirdPartyVadSpeech;
                ActiveSpeechSignal = activeSignal;
                UpdatedNoiseFloor = noiseFloor;
                UpdatedSilenceDurationSec = silenceDuration;
            }
        }
    }
}