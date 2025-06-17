using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Models;
using WebRtcVadSharp;

namespace VoiceAssistant
{
    public class AudioFrameProcessor : IAudioFrameProcessor
    {
        private readonly ILogger<AudioFrameProcessor> _logger;
        private WebRtcVad _vad;
        private VadSettings _vadSettings;
        private PipelineOptions _pipelineOptions;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;
        private const int FrameDurationMs = 20;
        private readonly int _frameBytes;
        private const int MinSpikeConfirmFrames = 2;

        private double _noiseFloor;
        private double _silenceDurationSec = 0;

        private Queue<byte[]> _preBuffer;
        private List<byte> _segmentBuffer;
        private bool _inSpeech;
        private int _consecSpeech;
        private int _consecSilence;
        private bool _potentialSpikeDetected;
        private bool _speechPrimedBySpike;
        
        private int _frameCount = 0; // For debugging

        public event Func<byte[], string, Task> SpeechSegmentDetected;

        public AudioFrameProcessor(ILogger<AudioFrameProcessor> logger, VadSettings initialVadSettings, PipelineOptions initialPipelineOptions)
        {
            _logger = logger;
            _vadSettings = initialVadSettings;
            _pipelineOptions = initialPipelineOptions;

            _frameBytes = SampleRate * Channels * BitsPerSample / 8 * FrameDurationMs / 1000;

            _vad = new WebRtcVad();
            ConfigureVad();

            _noiseFloor = MeasureInitialNoiseFloor();
            _logger.LogInformation("Initial noise floor: {NoiseFloor:F4}", _noiseFloor);

            _preBuffer = new Queue<byte[]>();
            _segmentBuffer = new List<byte>();
            _inSpeech = false;
            _consecSpeech = 0;
            _consecSilence = 0;
            _potentialSpikeDetected = false;
            _speechPrimedBySpike = false;

            LogCurrentSettings();
        }

        public void UpdateSettings(VadSettings vadSettings, PipelineOptions pipelineOptions)
        {
            bool vadSettingsChanged = !_vadSettings.Equals(vadSettings); // Requires VadSettings to implement Equals
            bool pipelineOptionsChanged = !_pipelineOptions.Equals(pipelineOptions); // Requires PipelineOptions to implement Equals

            if (vadSettingsChanged)
            {
                _vadSettings = vadSettings;
                ConfigureVad();
                double oldNoiseFloor = _noiseFloor;
                _noiseFloor = MeasureInitialNoiseFloor();
                if (Math.Abs(oldNoiseFloor - _noiseFloor) > 0.0001)
                {
                    _logger.LogInformation("Noise floor re-evaluated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} due to VAD settings update.", oldNoiseFloor, _noiseFloor);
                }
                _logger.LogInformation("VAD settings updated in AudioFrameProcessor.");
            }
            if (pipelineOptionsChanged)
            {
                _pipelineOptions = pipelineOptions;
                _logger.LogInformation("Pipeline options updated in AudioFrameProcessor.");
            }
            if (vadSettingsChanged || pipelineOptionsChanged)
            {
                LogCurrentSettings();
            }
        }

        private void ConfigureVad()
        {
            if (_vad == null) _vad = new WebRtcVad();
            _vad.OperatingMode = _vadSettings.OperatingMode;
            _vad.SampleRate = WebRtcVadSharp.SampleRate.Is16kHz;
            _vad.FrameLength = FrameLength.Is20ms;
            _logger.LogInformation("VAD configured with Mode: {Mode}, PreAmp: {PreAmp}, SpikeDetection: {SpikeDetection}, ThirdPartyVad: {ThirdPartyVad}",
                _vadSettings.OperatingMode, _vadSettings.PreAmplification, _vadSettings.EnableSpikeDetection, _vadSettings.EnableThirdPartyVad);
        }

        private double MeasureInitialNoiseFloor()
        {
            return _vadSettings.MinNoiseFloor;
        }

        private void LogCurrentSettings()
        {
             _logger.LogInformation(
                "Current VAD Settings (AudioFrameProcessor): Mode={Mode}, PreAmp={PreAmp:F1}, MinSpeech={MinSpeech:F2}s, PreSpeech={PreSpeech:F2}s, Hangover={Hangover:F2}s, SpikeDetection={SpikeDetection}, SpikeThreshold={SpikeThreshold}, ThirdPartyVad={ThirdPartyVad}, MinNoiseFloor={MinNoiseFloor}, NoiseFloorAlpha={NoiseFloorAlpha}, NoiseThresholdFactor={NoiseThresholdFactor}, SilenceAdaptationTimeSec={SilenceAdaptationTimeSec}, MinSegmentDurationSec={MinSegmentDurationSec}",
                _vadSettings.OperatingMode, _vadSettings.PreAmplification, _vadSettings.MinSpeechDurationSec,
                _vadSettings.PreSpeechDurationSec, _vadSettings.HangoverDurationSec, _vadSettings.EnableSpikeDetection,
                _vadSettings.VadSpikeThreshold, _vadSettings.EnableThirdPartyVad, _vadSettings.MinNoiseFloor, _vadSettings.NoiseFloorAlpha, _vadSettings.NoiseThresholdFactor, _vadSettings.SilenceAdaptationTimeSec, _vadSettings.MinSegmentDurationSec);

            _logger.LogInformation("Current Pipeline Options (AudioFrameProcessor): DisableVad={DisableVad}, DisableTts={DisableTts}, DisableProgressiveTts={DisableProgressiveTts}, TtsVoice={TtsVoice}, MinFirstChunk={MinFirst}, MaxFirst={MaxFirst}, SubsequentChunk={Subsequent}, DisableTokenStreaming={DisableTokenStreaming}, Language={Language}, ChatModel={ChatModel}",
                _pipelineOptions.DisableVad, _pipelineOptions.DisableTts, _pipelineOptions.DisableProgressiveTts, _pipelineOptions.TtsVoice,
                _pipelineOptions.TtsMinFirstChunkLength, _pipelineOptions.TtsMaxFirstChunkLength, _pipelineOptions.TtsSubsequentChunkLength, _pipelineOptions.DisableTokenStreaming, _pipelineOptions.Language, _pipelineOptions.ChatModel);
        }

        public async Task ProcessFrameAsync(byte[] audioFrame, string sessionId)
        {
            _frameCount++;
            if (_frameCount <= 10 || _frameCount % 100 == 0)
            {
                _logger.LogDebug("Session {SessionId}: Processing binary audio frame #{FrameCount} in AudioFrameProcessor", sessionId, _frameCount);
            }

            if (_pipelineOptions.DisableVad)
            {
                // If VAD is disabled, we might want to accumulate frames differently or bypass VAD logic.
                // For now, let's assume if VAD is disabled, frames are collected elsewhere and ProcessSegmentAsync is called directly.
                // This class's primary role is VAD, so if it's disabled, this method might not even be called with individual frames.
                // However, if it IS called, we can choose to buffer it and raise an event when connection closes.
                // For simplicity in this refactor, we'll assume the WebSocketHandler will manage raw audio accumulation when VAD is off.
                 _segmentBuffer.AddRange(audioFrame); // Collect all audio if VAD is disabled
                // The decision to call SpeechSegmentDetected will then need to be triggered by another mechanism (e.g., WebSocket close)
                return;
            }

            var frame = new byte[_frameBytes];
            Array.Copy(audioFrame, frame, _frameBytes);

            ApplyPreAmplification(frame);
            double frameRms = CalculateRms(frame);

            if (frameRms > 0.01)
            {
                _logger.LogDebug("Session {SessionId}: VAD Frame - RMS={FrameRms:F4}, NoiseFloor={NoiseFloor:F4}, SilenceDur={SilenceDur:F2}s", sessionId, frameRms, _noiseFloor, _silenceDurationSec);
            }

            _potentialSpikeDetected = false;
            if (_vadSettings.EnableSpikeDetection)
            {
                if (!_inSpeech && frameRms > _vadSettings.VadSpikeThreshold && frameRms > _noiseFloor * _vadSettings.NoiseThresholdFactor * 1.5)
                {
                    _potentialSpikeDetected = true;
                    _logger.LogTrace("Session {SessionId}: VAD spike detected - RMS: {FrameRms:F4} > SpikeThreshold: {SpikeThreshold:F4} && RMS > NoiseFloorFactor*1.5: {NoiseFloorFactorThreshold:F4}",
                                         sessionId, frameRms, _vadSettings.VadSpikeThreshold, _noiseFloor * _vadSettings.NoiseThresholdFactor * 1.5);
                }
            }

            bool hasSpeech = false;
            if (_vadSettings.EnableThirdPartyVad)
            {
                if (frame.Length == _frameBytes)
                {
                    hasSpeech = _vad.HasSpeech(frame);
                    _logger.LogTrace("Session {SessionId}: VAD Frame - WebRTC VAD HasSpeech={HasSpeech}", sessionId, hasSpeech);
                }
                else
                {
                    _logger.LogWarning("Session {SessionId}: VAD Frame - Invalid frame length for WebRTC VAD. Expected {ExpectedBytes}, Got {ActualBytes}. Assuming no speech.", sessionId, _frameBytes, frame.Length);
                    hasSpeech = false;
                }
            }
            else
            {
                _logger.LogTrace("Session {SessionId}: VAD Frame - WebRTC VAD disabled via EnableThirdPartyVad setting.", sessionId);
            }

            if (!hasSpeech)
                _silenceDurationSec += FrameDurationMs / 1000.0;
            else
                _silenceDurationSec = 0;

            if (!hasSpeech && _silenceDurationSec >= _vadSettings.SilenceAdaptationTimeSec)
            {
                double oldNoiseFloor = _noiseFloor;
                _noiseFloor = Math.Max(_vadSettings.MinNoiseFloor,
                    _vadSettings.NoiseFloorAlpha * _noiseFloor + (1 - _vadSettings.NoiseFloorAlpha) * frameRms);
                if (Math.Abs(oldNoiseFloor - _noiseFloor) > 0.0001)
                {
                    _logger.LogDebug("Session {SessionId}: VAD noise floor updated from {OldNoiseFloor:F4} to {NewNoiseFloor:F4} after {SilenceDurationSec:F2}s silence (Frame RMS: {FrameRms:F4})",
                                     sessionId, oldNoiseFloor, _noiseFloor, _silenceDurationSec, frameRms);
                }
            }

            double dynamicThreshold = Math.Max(_vadSettings.MinNoiseFloor, _noiseFloor * _vadSettings.NoiseThresholdFactor);
            bool isWebRtcSpeech = hasSpeech;
            bool isRmsAboveThreshold = frameRms >= dynamicThreshold;
            _logger.LogTrace("Session {SessionId}: VAD Frame - isWebRtcSpeech={IsWebRtcSpeech}, isRmsAboveThreshold={IsRmsAboveThreshold} (RMS: {FrameRms:F4}, DynThr: {DynamicThreshold:F4})",
                           sessionId, isWebRtcSpeech, isRmsAboveThreshold, frameRms, dynamicThreshold);

            bool activeSpeechSignal = false;
            if (_vadSettings.EnableThirdPartyVad && _vadSettings.EnableSpikeDetection)
            {
                activeSpeechSignal = (isWebRtcSpeech && isRmsAboveThreshold) || (_potentialSpikeDetected && isRmsAboveThreshold);
            }
            else if (_vadSettings.EnableThirdPartyVad)
            {
                activeSpeechSignal = isWebRtcSpeech && isRmsAboveThreshold;
            }
            else if (_vadSettings.EnableSpikeDetection)
            {
                activeSpeechSignal = _potentialSpikeDetected && isRmsAboveThreshold;
            }

            _preBuffer.Enqueue(frame);
            int preFrames = (int)(_vadSettings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            if (_preBuffer.Count > preFrames) _preBuffer.Dequeue();

            if (!_inSpeech)
            {
                if (activeSpeechSignal)
                {
                    _consecSpeech++;
                    if (_potentialSpikeDetected) _speechPrimedBySpike = true;

                    int startFrames = (int)(_vadSettings.MinSpeechDurationSec * 1000 / FrameDurationMs);
                    bool meetsStartCriteria = (_speechPrimedBySpike && _consecSpeech >= MinSpikeConfirmFrames) || (!_speechPrimedBySpike && _consecSpeech >= startFrames);

                    if (meetsStartCriteria)
                    {
                        _inSpeech = true;
                        _consecSpeech = _speechPrimedBySpike ? Math.Max(_consecSpeech, MinSpikeConfirmFrames) : _consecSpeech;
                        _consecSilence = 0;
                        _segmentBuffer.Clear();
                        foreach (var buf in _preBuffer) _segmentBuffer.AddRange(buf);
                        if (!_preBuffer.Contains(frame)) _segmentBuffer.AddRange(frame);
                        _logger.LogInformation("Session {SessionId}: VAD speech started (PrimedBySpike: {IsSpikeTriggered}, ConsecSpeechFrames: {ConsecSpeech}, RMS: {FrameRms:F4}, DynThr: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                             sessionId, _speechPrimedBySpike, _consecSpeech, frameRms, dynamicThreshold, isWebRtcSpeech);
                        _potentialSpikeDetected = false;
                        _speechPrimedBySpike = false;
                    }
                }
                else
                {
                    _consecSpeech = 0;
                    _potentialSpikeDetected = false;
                    _speechPrimedBySpike = false;
                }
            }
            else // _inSpeech == true
            {
                _segmentBuffer.AddRange(frame);
                int endFrames = (int)(_vadSettings.HangoverDurationSec * 1000 / FrameDurationMs);
                if (!activeSpeechSignal && ++_consecSilence >= endFrames)
                {
                    _inSpeech = false;
                    _logger.LogInformation("Session {SessionId}: VAD speech ended ({Bytes} bytes, ConsecSilenceFrames: {ConsecSilence}, RMS: {FrameRms:F4}, DynThr: {DynThr:F4}, WebRTC: {WebRtcSpeech})",
                                         sessionId, _segmentBuffer.Count, _consecSilence, frameRms, dynamicThreshold, isWebRtcSpeech);
                    
                    if (SpeechSegmentDetected != null)
                    {
                        await SpeechSegmentDetected.Invoke(_segmentBuffer.ToArray(), sessionId);
                    }
                    _segmentBuffer.Clear();
                    _consecSpeech = _consecSilence = 0;
                    _potentialSpikeDetected = false;
                    _speechPrimedBySpike = false;
                }
                else if (activeSpeechSignal)
                {
                    _consecSilence = 0;
                    _potentialSpikeDetected = false;
                    _speechPrimedBySpike = false;
                }
            }
        }
        
        // This method should be called when the WebSocket connection is closing,
        // to process any remaining audio in the buffer, especially if VAD was disabled or a segment was ongoing.
        public async Task ProcessRemainingAudioAsync(string sessionId)
        {
            if (_pipelineOptions.DisableVad && _segmentBuffer.Count > 0)
            {
                _logger.LogDebug("Session {SessionId}: VAD disabled, processing all received audio ({Bytes} bytes) on close from AudioFrameProcessor.", sessionId, _segmentBuffer.Count);
                if (SpeechSegmentDetected != null)
                {
                    await SpeechSegmentDetected.Invoke(_segmentBuffer.ToArray(), sessionId);
                }
                _segmentBuffer.Clear();
            }
            else if (_inSpeech && _segmentBuffer.Count > 0) // VAD enabled and speech was ongoing
            {
                 _logger.LogDebug("Session {SessionId}: WebSocket closing, processing final VAD segment ({Bytes} bytes) from AudioFrameProcessor.", sessionId, _segmentBuffer.Count);
                if (SpeechSegmentDetected != null)
                {
                    await SpeechSegmentDetected.Invoke(_segmentBuffer.ToArray(), sessionId);
                }
                _segmentBuffer.Clear();
            }
             _inSpeech = false; // Reset state
            _consecSpeech = 0;
            _consecSilence = 0;
        }


        private void ApplyPreAmplification(byte[] frame)
        {
            if (Math.Abs(_vadSettings.PreAmplification - 1.0f) < 0.001f) return;
            for (int i = 0; i < frame.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(frame, i);
                int amplified = (int)(sample * _vadSettings.PreAmplification);
                // Replace Math.Clamp with manual clamping
                if (amplified > short.MaxValue) amplified = short.MaxValue;
                else if (amplified < short.MinValue) amplified = short.MinValue;
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
    }
}
