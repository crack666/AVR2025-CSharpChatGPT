using System.Net.WebSockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VoiceAssistant; // Corrected: For IWebSocketHandler, IAudioFrameProcessor, etc.

namespace VoiceAssistant
{
    /// <summary>
    /// Orchestrates WebSocket audio handling by delegating to an IWebSocketHandler.
    /// This class is simplified and relies on DI to provide a scoped IWebSocketHandler.
    /// </summary>
    public class WebSocketAudioService
    {
        private readonly ILogger<WebSocketAudioService> _logger;
        private readonly IWebSocketHandler _webSocketHandler;

        public WebSocketAudioService(
            ILogger<WebSocketAudioService> logger,
            IWebSocketHandler webSocketHandler) // Injected scoped handler
        {
            _logger = logger;
            _webSocketHandler = webSocketHandler;
            _logger.LogInformation("WebSocketAudioService initialized, will use injected IWebSocketHandler.");
        }

        /// <summary>
        /// Handles an incoming WebSocket connection by delegating to the IWebSocketHandler.
        /// </summary>
        /// <param name="webSocket">The WebSocket connection.</param>
        /// <param name="sessionId">A unique identifier for the session.</param>
        public async Task HandleAsync(WebSocket webSocket, string sessionId)
        {
            _logger.LogInformation("Session {SessionId}: WebSocketAudioService delegating to IWebSocketHandler.", sessionId);
            // The _webSocketHandler is already configured with session-specific dependencies (options)
            // as it was resolved from a scope that had these options initialized.
            await _webSocketHandler.HandleAsync(webSocket, sessionId);
        }
    }
}