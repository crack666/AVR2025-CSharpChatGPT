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
        private string _currentSessionId; // Store session ID for event handlers

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
            _vadSettings = initialVadSettings;            // Subscribe to events from the processors
            _audioFrameProcessor.SpeechSegmentDetected += OnSpeechSegmentDetected;
            _audioSegmentProcessor.OnTranscriptionReady += OnTranscriptionReadyHandler;
            _audioSegmentProcessor.OnTokenReady += OnTokenReadyHandler;
            _audioSegmentProcessor.OnAudioChunkReady += OnAudioChunkReadyHandler;
            _audioSegmentProcessor.OnError += OnErrorHandler;
            _audioSegmentProcessor.OnDone += OnDoneHandler;

            // Initialize AudioFrameProcessor with current settings
            _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
        }        // Event Handlers for AudioSegmentProcessor events
        private Task OnTranscriptionReadyHandler(string sessionId, string transcription)
        {
            if (sessionId == _currentSessionId && _currentWebSocket != null)
            {
                _logger.LogInformation("Session {SessionId}: Transcription ready: '{Transcription}'", sessionId, transcription);
                return SendEventAsync(_currentWebSocket, "transcription", new { text = transcription });
            }
            _logger.LogWarning("Session {SessionId}: Received transcription for inactive/mismatched session.", sessionId);
            return Task.CompletedTask;
        }        private Task OnTokenReadyHandler(string token, string sessionId)
        {
            if (sessionId == _currentSessionId && _currentWebSocket != null)
            {
                _logger.LogDebug("Session {SessionId}: Token ready: '{Token}'", sessionId, token);
                return SendEventAsync(_currentWebSocket, "token", new { token = token });
            }
            _logger.LogWarning("Session {SessionId}: Received token for inactive/mismatched session.", sessionId);
            return Task.CompletedTask;
        }

        private Task OnAudioChunkReadyHandler(byte[] audioBytes, int chunkIndex, string sessionId)
        {
            if (sessionId == _currentSessionId && _currentWebSocket != null)
            {
                _logger.LogDebug("Session {SessionId}: Audio chunk ready, index {ChunkIndex}", sessionId, chunkIndex);
                return SendAudioChunkAsync(_currentWebSocket, audioBytes, chunkIndex, sessionId);
            }
            _logger.LogWarning("Session {SessionId}: Received audio chunk for inactive/mismatched session.", sessionId);
            return Task.CompletedTask;
        }        private Task OnErrorHandler(string sessionId, string errorMessage)
        {
            if (sessionId == _currentSessionId && _currentWebSocket != null)
            {
                _logger.LogError("Session {SessionId}: Error from AudioSegmentProcessor: {ErrorMessage}", sessionId, errorMessage);
                return SendEventAsync(_currentWebSocket, "error", new { message = errorMessage });
            }
            _logger.LogWarning("Session {SessionId}: Received error for inactive/mismatched session.", sessionId);
            return Task.CompletedTask;
        }

        private Task OnDoneHandler(string sessionId, object performanceMetrics, string _)
        {
            if (sessionId == _currentSessionId && _currentWebSocket != null)
            {
                _logger.LogInformation("Session {SessionId}: Processing completed, sending done message", sessionId);
                return SendEventAsync(_currentWebSocket, "done", new { payload = performanceMetrics });
            }
            _logger.LogWarning("Session {SessionId}: Received done event for inactive/mismatched session.", sessionId);
            return Task.CompletedTask;
        }

        private async Task OnSpeechSegmentDetected(byte[] audioBytes, string sessionId)
        {
            if (_currentWebSocket != null && _currentWebSocket.State == WebSocketState.Open)
            {
                _logger.LogDebug("Session {SessionId}: Speech segment detected by AudioFrameProcessor. Processing via AudioSegmentProcessor.", sessionId);
                await _audioSegmentProcessor.ProcessSegmentAsync(audioBytes, sessionId, _pipelineOptions, _vadSettings);
            }
            else
            {
                _logger.LogWarning("Session {SessionId}: Speech segment detected but WebSocket is not open or available. Segment will not be processed.", sessionId);
            }
        }

        public async Task HandleAsync(WebSocket webSocket, string sessionId)
        {
            _currentWebSocket = webSocket; // Store the WebSocket for the current session
            _currentSessionId = sessionId; // Store session ID
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
                    // TEMP DEBUG: Log to confirm the loop is running and waiting for data.
                    _logger.LogTrace("Session {SessionId}: Loop entered, waiting for ReceiveAsync...", sessionId);

                    var receiveSegment = new ArraySegment<byte>(messageReceiveBuffer);
                    WebSocketReceiveResult result;
                    try
                    {
                        result = await _currentWebSocket.ReceiveAsync(receiveSegment, CancellationToken.None);
                        
                        // TEMP DEBUG: Log upon receiving any data.
                        if (result.Count > 0) {
                            _logger.LogTrace("Session {SessionId}: ReceiveAsync returned with {Count} bytes, MessageType: {MessageType}", sessionId, result.Count, result.MessageType);
                        }
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
                            await _audioSegmentProcessor.ProcessSegmentAsync(rawAudioBufferForDisabledVad.ToArray(), sessionId, _pipelineOptions, _vadSettings);
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
                                                var newVadSettings = _webSocketSettingsManager.HandleUpdateVadSettings(payloadElement);
                                                if (newVadSettings != null)
                                                {
                                                    _vadSettings = newVadSettings;
                                                    _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
                                                    await SendEventAsync(_currentWebSocket, "vad_settings_updated", _vadSettings);
                                                }
                                                break;
                                            case "updatepipelineoptions":
                                                var newPipelineOptions = _webSocketSettingsManager.HandleUpdatePipelineOptions(payloadElement);
                                                if (newPipelineOptions != null)
                                                {
                                                    _pipelineOptions = newPipelineOptions;
                                                    _audioFrameProcessor.UpdateSettings(_vadSettings, _pipelineOptions);
                                                    await SendEventAsync(_currentWebSocket, "pipeline_options_updated", _pipelineOptions);
                                                }
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
                // Unsubscribe from all events
                _audioFrameProcessor.SpeechSegmentDetected -= OnSpeechSegmentDetected;
                _audioSegmentProcessor.OnTranscriptionReady -= OnTranscriptionReadyHandler;
                _audioSegmentProcessor.OnAudioChunkReady -= OnAudioChunkReadyHandler;
                _audioSegmentProcessor.OnError -= OnErrorHandler;

                _logger.LogInformation("Session {SessionId}: WebSocket connection closed in WebSocketHandler. Final State: {State}, CloseStatus: {CloseStatus}, Description: {Description}, Exception: {Exception}",
                    sessionId,
                    _currentWebSocket?.State,
                    closeStatus,
                    closeStatusDescription,
                    finalException?.ToString() ?? "<none>");
                _currentWebSocket = null; // Clear the stored WebSocket instance
                _currentSessionId = null; // Clear session ID
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
            }            if (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                _logger.LogDebug("Session {SessionId}: Sending audio chunk - Index={ChunkIndex}, Size={SizeBytes} bytes", sessionId, chunkIndex, audioBytes.Length);
                try
                {
                    // Send info message BEFORE binary data so frontend knows the index
                    await SendEventAsync(webSocket, "audio-chunk-info", new { index = chunkIndex, size = audioBytes.Length });
                    await webSocket.SendAsync(new ArraySegment<byte>(audioBytes), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Session {SessionId}: Error sending audio chunk {ChunkIndex}", sessionId, chunkIndex);
                }
            }
        }
    }
}

