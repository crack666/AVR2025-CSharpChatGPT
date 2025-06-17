using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public class WebSocketSettingsManager : IWebSocketSettingsManager
    {
        private readonly ILogger<WebSocketSettingsManager> _logger;
        private readonly IWebSocketHandler _webSocketHandler; // To send confirmation messages

        public WebSocketSettingsManager(ILogger<WebSocketSettingsManager> logger, IWebSocketHandler webSocketHandler)
        {
            _logger = logger;
            _webSocketHandler = webSocketHandler;
        }

        public async Task HandleUpdateVadSettingsAsync(JsonElement payload, WebSocket webSocket, VadSettings currentVadSettings, PipelineOptions currentPipelineOptions, Action<VadSettings> onVadSettingsUpdated)
        {
            _logger.LogInformation("Attempting to update VAD settings from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedSettings = JsonSerializer.Deserialize<VadSettings>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedSettings != null)
                {
                    onVadSettingsUpdated(updatedSettings); // Callback to update the actual settings
                    _logger.LogInformation("VAD settings updated successfully via callback.");
                    LogCurrentSettings(updatedSettings, currentPipelineOptions); // Log new settings
                    await _webSocketHandler.SendEventAsync(webSocket, "vad_settings_updated", updatedSettings);
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize VAD settings payload into a non-null object.");
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing VAD settings payload.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating VAD settings.");
            }
        }

        public async Task HandleUpdatePipelineOptionsAsync(JsonElement payload, WebSocket webSocket, PipelineOptions currentPipelineOptions, Action<PipelineOptions> onPipelineOptionsUpdated)
        {
            _logger.LogInformation("Attempting to update Pipeline options from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedOptions = JsonSerializer.Deserialize<PipelineOptions>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedOptions != null)
                {
                    onPipelineOptionsUpdated(updatedOptions); // Callback to update the actual options
                    _logger.LogInformation("Pipeline options updated successfully via callback.");
                    // Assuming VadSettings are not changed here, so pass the current ones.
                    // If VadSettings could also be part of this payload, the logging and callback would need adjustment.
                    // For now, assuming HandleUpdateVadSettingsAsync handles VadSettings exclusively.
                    // LogCurrentSettings(currentVadSettings, updatedOptions); // Log new settings
                    await _webSocketHandler.SendEventAsync(webSocket, "pipeline_options_updated", updatedOptions);
                }
                else
                {
                    _logger.LogWarning("Failed to deserialize Pipeline options payload into a non-null object.");
                }
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing Pipeline options payload.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Pipeline options.");
            }
        }

        private void LogCurrentSettings(VadSettings vadSettings, PipelineOptions pipelineOptions)
        {
            _logger.LogInformation(
                "Current VAD Settings: Mode={Mode}, PreAmp={PreAmp:F1}, MinSpeech={MinSpeech:F2}s, PreSpeech={PreSpeech:F2}s, Hangover={Hangover:F2}s, SpikeDetection={SpikeDetection}, SpikeThreshold={SpikeThreshold}, ThirdPartyVad={ThirdPartyVad}, MinNoiseFloor={MinNoiseFloor}, NoiseFloorAlpha={NoiseFloorAlpha}, NoiseThresholdFactor={NoiseThresholdFactor}, SilenceAdaptationTimeSec={SilenceAdaptationTimeSec}, MinSegmentDurationSec={MinSegmentDurationSec}",
                vadSettings.OperatingMode, vadSettings.PreAmplification, vadSettings.MinSpeechDurationSec,
                vadSettings.PreSpeechDurationSec, vadSettings.HangoverDurationSec, vadSettings.EnableSpikeDetection,
                vadSettings.VadSpikeThreshold, vadSettings.EnableThirdPartyVad, vadSettings.MinNoiseFloor, vadSettings.NoiseFloorAlpha, vadSettings.NoiseThresholdFactor, vadSettings.SilenceAdaptationTimeSec, vadSettings.MinSegmentDurationSec);

            _logger.LogInformation("Current Pipeline Options: DisableVad={DisableVad}, DisableTts={DisableTts}, DisableProgressiveTts={DisableProgressiveTts}, TtsVoice={TtsVoice}, MinFirstChunk={MinFirst}, MaxFirst={MaxFirst}, SubsequentChunk={Subsequent}, DisableTokenStreaming={DisableTokenStreaming}, Language={Language}, ChatModel={ChatModel}",
                pipelineOptions.DisableVad, pipelineOptions.DisableTts, pipelineOptions.DisableProgressiveTts, pipelineOptions.TtsVoice,
                pipelineOptions.TtsMinFirstChunkLength, pipelineOptions.TtsMaxFirstChunkLength, pipelineOptions.TtsSubsequentChunkLength, pipelineOptions.DisableTokenStreaming, pipelineOptions.Language, pipelineOptions.ChatModel);
        }
    }
}
