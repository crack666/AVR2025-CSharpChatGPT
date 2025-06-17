using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceAssistant.Core.Models; // Required for PipelineOptions and VadSettings

namespace VoiceAssistant
{
    public class WebSocketHandler : IWebSocketHandler
    {
        private readonly ILogger<WebSocketHandler> _logger;
        private readonly IAudioFrameProcessor _audioFrameProcessor;
        private readonly IAudioSegmentProcessor _audioSegmentProcessor;
        private readonly IWebSocketSettingsManager _webSocketSettingsManager;
        private PipelineOptions _pipelineOptions; // Instance specific
        private VadSettings _vadSettings;       // Instance specific

        private const int FrameBytes = 16000 * 1 * 16 / 8 * 20 / 1000; // 16kHz, 1ch, 16bit, 20ms

        // Store the WebSocket instance for the session to be used by OnSpeechSegmentDetected
        private WebSocket _currentWebSocket;

        public WebSocketHandler(
        ILogger<WebSocketHandler> logger,
        IAudioFrameProcessor audioFrameProcessor,
        IAudioSegmentProcessor audioSegmentProcessor,
        IWebSocketSettingsManager webSocketSettingsManager,
        PipelineOptions initialPipelineOptions, // Injected per session
        VadSettings initialVadSettings)         // Injected per session
        {
            _logger = logger;
            _audioFrameProcessor = audioFrameProcessor;
            _audioSegmentProcessor = audioSegmentProcessor;
            _webSocketSettingsManager = webSocketSettingsManager;
            _pipelineOptions = initialPipelineOptions;
            _vadSettings = initialVadSettings;

            // Subscribe to the SpeechSegmentDetected event from AudioFrameProcessor
            _audioFrameProcessor.SpeechSegmentDetected += OnSpeechSegmentDetected;
            // Initialize AudioFrameProcessor with current settings
            _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
        }

        private async Task OnSpeechSegmentDetected(byte[] audioBytes, string sessionId)
        {
            if (_currentWebSocket != null && _currentWebSocket.State == WebSocketState.Open)
            {
                _logger.LogDebug("Session {SessionId}: Speech segment detected by AudioFrameProcessor. Processing via AudioSegmentProcessor.", sessionId);
                await _audioSegmentProcessor.ProcessSegmentAsync(audioBytes, _currentWebSocket, sessionId, _pipelineOptions, _vadSettings);
            }
            else
            {
                _logger.LogWarning("Session {SessionId}: Speech segment detected but WebSocket is not open or available. Segment will not be processed.", sessionId);
            }
        }

        public async Task HandleAsync(WebSocket webSocket, string sessionId)
        {
            _currentWebSocket = webSocket; // Store the WebSocket for the current session
            _logger.LogInformation("Session {SessionId}: WebSocket connected to WebSocketHandler. State: {State}", sessionId, webSocket.State);
            var rawAudioBufferForDisabledVad = new List<byte>();
            var messageReceiveBuffer = new byte[8192];
            var binaryMessageBuffer = new List<byte>();
            WebSocketCloseStatus? closeStatus = null;
            string closeStatusDescription = null;
            Exception finalException = null;
            try
            {
                while (_currentWebSocket.State == WebSocketState.Open)
                {
                    var receiveSegment = new ArraySegment<byte>(messageReceiveBuffer);
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _currentWebSocket.ReceiveAsync(receiveSegment, CancellationToken.None);
                    }
                    catch (WebSocketException wsex)
                    {
                        _logger.LogError(wsex, "Session {SessionId}: WebSocketException during ReceiveAsync. Ending session.", sessionId);
                        finalException = wsex;
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeStatus = result.CloseStatus;
                        closeStatusDescription = result.CloseStatusDescription;
                        _logger.LogInformation("Session {SessionId}: WebSocket close message received. CloseStatus: {CloseStatus}, Description: {Description}", sessionId, closeStatus, closeStatusDescription);
                        // Process any remaining audio
                        if (_pipelineOptions.DisableVad && rawAudioBufferForDisabledVad.Count > 0)
                        {
                            _logger.LogDebug("Session {SessionId}: VAD disabled, processing all received audio ({Bytes} bytes) on close.", sessionId, rawAudioBufferForDisabledVad.Count);
                            await _audioSegmentProcessor.ProcessSegmentAsync(rawAudioBufferForDisabledVad.ToArray(), _currentWebSocket, sessionId, _pipelineOptions, _vadSettings);
                            rawAudioBufferForDisabledVad.Clear();
                        }
                        else if (!_pipelineOptions.DisableVad)
                        {
                            // If VAD is enabled, AudioFrameProcessor might have a pending segment.
                            // We need a way to tell AudioFrameProcessor to process its final buffer.
                            if (_audioFrameProcessor is AudioFrameProcessor afp) // Check if it\'s the concrete type to call the new method
                            {
                                await afp.ProcessRemainingAudioAsync(sessionId);
                            }
                        }
                        try { await _currentWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                        catch (WebSocketException ex) { _logger.LogWarning(ex, "Session {SessionId}: WebSocketException during CloseAsync.", sessionId); finalException = ex; }
                        break;
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        if (receiveSegment.Array == null) continue;
                        string messageJson = Encoding.UTF8.GetString(receiveSegment.Array, receiveSegment.Offset, result.Count);
                        _logger.LogDebug("Session {SessionId}: Received text message: {MessageJson}", sessionId, messageJson);
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(messageJson))
                            {
                                JsonElement root = doc.RootElement;
                                if (root.TryGetProperty("type", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.String)
                                {
                                    string messageType = typeElement.GetString();
                                    if (root.TryGetProperty("payload", out JsonElement payloadElement))
                                    {
                                        switch (messageType?.ToLowerInvariant())
                                        {
                                            case "updatevadsettings":
                                            case "vad_settings":
                                                await _webSocketSettingsManager.HandleUpdateVadSettingsAsync(payloadElement, _currentWebSocket, _vadSettings, _pipelineOptions, (newSettings) =>
                                                {
                                                    _vadSettings = newSettings;
                                                    _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
                                                });
                                                break;
                                            case "updatepipelineoptions":
                                                await _webSocketSettingsManager.HandleUpdatePipelineOptionsAsync(payloadElement, _currentWebSocket, _pipelineOptions, (newOptions) =>
                                                {
                                                    _pipelineOptions = newOptions;
                                                    _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
                                                });
                                                break;
                                            default:
                                                _logger.LogWarning("Session {SessionId}: Unknown WebSocket message type: {MessageType}", sessionId, messageType);
                                                break;
                                        }
                                    }
                                    else { _logger.LogWarning("Session {SessionId}: WebSocket message missing payload: {MessageJson}", sessionId, messageJson); }
                                }
                                else { _logger.LogWarning("Session {SessionId}: WebSocket message missing type or type is not a string: {MessageJson}", sessionId, messageJson); }
                            }
                        }
                        catch (JsonException jsonEx) { _logger.LogError(jsonEx, "Session {SessionId}: Error deserializing WebSocket message: {MessageJson}", sessionId, messageJson); }
                        catch (Exception ex) { _logger.LogError(ex, "Session {SessionId}: Error processing WebSocket message: {MessageJson}", sessionId, messageJson); }
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        if (receiveSegment.Array == null) continue;
                        binaryMessageBuffer.AddRange(new ArraySegment<byte>(receiveSegment.Array, receiveSegment.Offset, result.Count));

                        if (result.EndOfMessage)
                        {
                            var completeFrame = binaryMessageBuffer.ToArray();
                            binaryMessageBuffer.Clear();

                            if (completeFrame.Length != FrameBytes)
                            {
                                _logger.LogWarning("Session {SessionId}: Received complete binary message with unexpected size. Expected {ExpectedSize}, got {ActualSize}. Skipping.", sessionId, FrameBytes, completeFrame.Length);
                                continue;
                            }

                            if (_pipelineOptions.DisableVad)
                            {
                                rawAudioBufferForDisabledVad.AddRange(completeFrame);
                            }
                            else
                            {
                                // Pass the frame to AudioFrameProcessor. 
                                // AudioFrameProcessor will raise SpeechSegmentDetected event when a segment is ready.
                                // The event handler will then call AudioSegmentProcessor.
                                await _audioFrameProcessor.ProcessFrameAsync(completeFrame, sessionId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Unhandled exception in WebSocketHandler.HandleAsync.", sessionId);
                finalException = ex;
            }
            finally
            {
                _audioFrameProcessor.SpeechSegmentDetected -= OnSpeechSegmentDetected; // Unsubscribe
                _logger.LogInformation("Session {SessionId}: WebSocket connection closed in WebSocketHandler. Final State: {State}, CloseStatus: {CloseStatus}, Description: {Description}, Exception: {Exception}",
                    sessionId,
                    _currentWebSocket?.State,
                    closeStatus,
                    closeStatusDescription,
                    finalException?.ToString() ?? "<none>");
                _currentWebSocket = null; // Clear the stored WebSocket instance
            }
        }

        public async Task SendEventAsync(WebSocket webSocket, string eventName, object payload)
        {
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var eventMessage = new { type = eventName, payload = payload };
                    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    var messageJson = JsonSerializer.Serialize(eventMessage, options);
                    var messageBytes = Encoding.UTF8.GetBytes(messageJson);
                    await webSocket.SendAsync(new ArraySegment<byte>(messageBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogTrace("Sent event details: Type=''{EventType}'', Payload=''{PayloadJson}''", eventName, messageJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sending event: {EventName}", eventName);
                }
            }
        }

        public async Task SendAudioChunkAsync(WebSocket webSocket, byte[] audioBytes, int chunkIndex, string sessionId)
        {
            if (audioBytes.Length == 0)
            {
                _logger.LogWarning("Session {SessionId}: Audio chunk {ChunkIndex} is empty, not sending.", sessionId, chunkIndex);
                return;
            }
            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                _logger.LogDebug("Session {SessionId}: Sending audio chunk - Index={ChunkIndex}, Size={SizeBytes} bytes", sessionId, chunkIndex, audioBytes.Length);
                try
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(audioBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                    await SendEventAsync(webSocket, "audio-chunk-info", new { index = chunkIndex, size = audioBytes.Length });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {SessionId}: Error sending audio chunk {ChunkIndex}", sessionId, chunkIndex);
                }
            }
        }
    }
}

