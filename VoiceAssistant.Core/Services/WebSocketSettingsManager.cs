using Microsoft.Extensions.Logging;
using System.Text.Json;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public class WebSocketSettingsManager : IWebSocketSettingsManager
    {
        private readonly ILogger<WebSocketSettingsManager> _logger;

        public WebSocketSettingsManager(ILogger<WebSocketSettingsManager> logger)
        {
            _logger = logger;
        }

        public VadSettings HandleUpdateVadSettings(JsonElement payload)
        {
            _logger.LogInformation("Attempting to update VAD settings from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedSettings = JsonSerializer.Deserialize<VadSettings>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedSettings != null)
                {
                    _logger.LogInformation("VAD settings deserialized successfully.");
                    return updatedSettings;
                }

                _logger.LogWarning("Failed to deserialize VAD settings payload into a non-null object.");
                return null;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing VAD settings payload.");
                return null;
            }
        }

        public PipelineOptions HandleUpdatePipelineOptions(JsonElement payload)
        {
            _logger.LogInformation("Attempting to update Pipeline options from payload: {Payload}", payload.GetRawText());
            try
            {
                var updatedOptions = JsonSerializer.Deserialize<PipelineOptions>(payload.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (updatedOptions != null)
                {
                    _logger.LogInformation("Pipeline options deserialized successfully.");
                    return updatedOptions;
                }

                _logger.LogWarning("Failed to deserialize Pipeline options payload into a non-null object.");
                return null;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Error deserializing Pipeline options payload.");
                return null;
            }
        }
    }
}
