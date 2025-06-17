using System.Net.WebSockets;
using System.Threading.Tasks;

namespace VoiceAssistant
{
    public interface IWebSocketHandler
    {
        Task HandleAsync(WebSocket webSocket, string sessionId);
        Task SendEventAsync(WebSocket webSocket, string eventName, object payload);
        Task SendAudioChunkAsync(WebSocket webSocket, byte[] audioBytes, int chunkIndex, string sessionId);
    }
}
