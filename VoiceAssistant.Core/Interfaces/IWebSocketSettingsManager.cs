using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public interface IWebSocketSettingsManager
    {
        Task HandleUpdateVadSettingsAsync(JsonElement payload, WebSocket webSocket, VadSettings currentVadSettings, PipelineOptions currentPipelineOptions, Action<VadSettings> onVadSettingsUpdated);
        Task HandleUpdatePipelineOptionsAsync(JsonElement payload, WebSocket webSocket, PipelineOptions currentPipelineOptions, Action<PipelineOptions> onPipelineOptionsUpdated);
    }
}
