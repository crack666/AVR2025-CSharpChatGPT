using System.Text.Json;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public interface IWebSocketSettingsManager
    {
        VadSettings HandleUpdateVadSettings(JsonElement payload);
        PipelineOptions HandleUpdatePipelineOptions(JsonElement payload);
    }
}
