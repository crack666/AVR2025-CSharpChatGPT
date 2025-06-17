using System.Net.WebSockets;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models;

namespace VoiceAssistant
{
    public interface IAudioSegmentProcessor
    {
        Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket, string sessionId, PipelineOptions pipelineOptions, VadSettings vadSettings);
    }
}
