#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace VoiceAssistant.Plugins.OpenAI
{
    /// <summary>
    /// OpenAI Realtime API implementation for true streaming recognition with built-in VAD.
    /// This uses WebSocket connection to wss://api.openai.com/v1/realtime
    /// </summary>
    public class OpenAIRealtimeRecognizer : IRecognizer, IDisposable
    {
        private readonly ILogger<OpenAIRealtimeRecognizer> _logger;
        private readonly string _apiKey;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isConnected = false;
        private string? _currentSessionId;
        
        // Events for streaming results
        public event Func<string, string, bool, Task>? OnTranscriptionReady; // text, sessionId, isPartial
        public event Func<string, Task>? OnSpeechStarted; // sessionId
        public event Func<string, Task>? OnSpeechEnded; // sessionId
        public event Func<string, string, Task>? OnError; // sessionId, error

        /// <summary>
        /// Check if realtime API is connected
        /// </summary>
        public bool IsRealtimeConnected => _isConnected;

        public OpenAIRealtimeRecognizer(string apiKey, ILogger<OpenAIRealtimeRecognizer> logger)
        {
            _apiKey = apiKey;
            _logger = logger;
        }

        /// <summary>
        /// Traditional recognition method - converts audio to stream and processes
        /// </summary>
        public async Task<string> RecognizeAsync(Stream audioStream, string language, string? contentType = null, string? fileName = null)
        {
            // For compatibility, we'll convert the stream to bytes and use streaming method
            using var memoryStream = new MemoryStream();
            await audioStream.CopyToAsync(memoryStream);
            byte[] audioBytes = memoryStream.ToArray();
            
            return await RecognizeStreamingAsync(audioBytes, language, false);
        }

        /// <summary>
        /// Streaming recognition using chunks - fallback method
        /// </summary>
        public async Task<string> RecognizeStreamingAsync(byte[] audioChunk, string language, bool isPartial = true)
        {
            if (!_isConnected)
            {
                _logger.LogWarning("Realtime API not connected, cannot process audio chunk");
                return string.Empty;
            }

            try
            {
                // Convert audio bytes to base64
                string base64Audio = Convert.ToBase64String(audioChunk);
                
                // Send audio chunk to Realtime API
                var appendEvent = new
                {
                    type = "input_audio_buffer.append",
                    audio = base64Audio
                };

                await SendEventAsync(appendEvent);

                if (!isPartial)
                {
                    // Commit the buffer for final processing
                    await SendEventAsync(new { type = "input_audio_buffer.commit" });
                    await SendEventAsync(new { type = "response.create" });
                }

                // For now, return empty - actual results come through events
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in streaming recognition");
                return string.Empty;
            }
        }        /// <summary>
        /// Real-time recognition using OpenAI Realtime API with WebSocket
        /// </summary>
        public async Task<string> RecognizeRealtimeAsync(byte[] audioChunk, string language, string sessionId)
        {
            _logger.LogTrace("Session {SessionId}: RecognizeRealtimeAsync called with {ByteCount} bytes", sessionId, audioChunk?.Length ?? 0);
            
            if (!_isConnected || _currentSessionId != sessionId)
            {
                _logger.LogInformation("Session {SessionId}: Not connected or session mismatch (current: {CurrentSessionId}), connecting...", 
                    sessionId, _currentSessionId);
                await ConnectAsync(sessionId, language);
            }

            // Convert audio to base64 and send
            string base64Audio = Convert.ToBase64String(audioChunk);
              _logger.LogTrace("Session {SessionId}: Sending {ByteCount} bytes ({Base64Length} base64 chars) to Realtime API", 
                sessionId, audioChunk?.Length ?? 0, base64Audio.Length);
            
            var appendEvent = new
            {
                type = "input_audio_buffer.append",
                audio = base64Audio
            };

            await SendEventAsync(appendEvent);
            _logger.LogTrace("Session {SessionId}: Audio chunk sent to Realtime API", sessionId);
            
            // Results come through events, not return value
            return string.Empty;
        }        /// <summary>
        /// Connect to OpenAI Realtime API for transcription-only use case
        /// </summary>
        public async Task ConnectAsync(string sessionId, string language = "en")
        {
            try
            {
                _logger.LogInformation("Session {SessionId}: Starting connection to OpenAI Realtime API...", sessionId);
                
                _currentSessionId = sessionId;
                _cancellationTokenSource = new CancellationTokenSource();
                _webSocket = new ClientWebSocket();

                // Set headers for Realtime API
                _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                _webSocket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

                _logger.LogInformation("Session {SessionId}: WebSocket headers set, connecting to OpenAI...", sessionId);
                
                // Connect to transcription endpoint
                var uri = new Uri("wss://api.openai.com/v1/realtime?intent=transcription");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);

                _logger.LogInformation("Session {SessionId}: WebSocket connection established, state: {State}", sessionId, _webSocket.State);

                _isConnected = true;
                _logger.LogInformation("Session {SessionId}: Connected to OpenAI Realtime API for transcription", sessionId);

                // Configure session for transcription
                var sessionUpdate = new
                {
                    type = "session.update",
                    session = new
                    {
                        modalities = new[] { "text" }, // Transcription only
                        instructions = "You are a transcription service. Transcribe the audio accurately.",
                        voice = "alloy",
                        input_audio_format = "pcm16",
                        output_audio_format = "pcm16",
                        input_audio_transcription = new
                        {
                            model = "whisper-1"
                        },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 200,
                            create_response = false // We only want transcription, not responses
                        }
                    }
                };

                _logger.LogInformation("Session {SessionId}: Sending session configuration to OpenAI...", sessionId);
                await SendEventAsync(sessionUpdate);
                _logger.LogInformation("Session {SessionId}: Session configuration sent to OpenAI Realtime API", sessionId);

                // Start listening for events
                _logger.LogInformation("Session {SessionId}: Starting event listener task for OpenAI Realtime API", sessionId);
                _ = Task.Run(async () => {
                    try 
                    {
                        _logger.LogInformation("Session {SessionId}: Event listener task started", sessionId);
                        await ListenForEventsAsync(_cancellationTokenSource.Token);
                        _logger.LogInformation("Session {SessionId}: Event listener task completed", sessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Session {SessionId}: Event listener task failed", sessionId);
                    }
                });
                
                _logger.LogInformation("Session {SessionId}: OpenAI Realtime API connection setup completed", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Failed to connect to OpenAI Realtime API", sessionId);
                _isConnected = false;
                throw;
            }
        }

        /// <summary>
        /// Disconnect from Realtime API
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                _isConnected = false;
                _cancellationTokenSource?.Cancel();

                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during disconnect");
            }
            finally
            {
                _webSocket?.Dispose();
                _webSocket = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _currentSessionId = null;
            }
        }

        /// <summary>
        /// Send event to Realtime API
        /// </summary>        
        private async Task SendEventAsync(object eventData)
        {
            if (_webSocket?.State != WebSocketState.Open)
            {
                _logger.LogWarning("Session {SessionId}: WebSocket not open (state: {State}), cannot send event", _currentSessionId, _webSocket?.State);
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(eventData, new JsonSerializerOptions { WriteIndented = false });
                _logger.LogInformation("Session {SessionId}: Sending event to OpenAI: {Json}", _currentSessionId, json);
                
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                
                _logger.LogInformation("Session {SessionId}: Successfully sent event with {ByteCount} bytes", _currentSessionId, bytes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Error sending event to Realtime API", _currentSessionId);
            }
        }

        /// <summary>
        /// Listen for events from Realtime API
        /// </summary>
        private async Task ListenForEventsAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            _logger.LogInformation("Session {SessionId}: Starting to listen for Realtime API events", _currentSessionId);
            
            try
            {
                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogTrace("Session {SessionId}: Waiting for WebSocket message...", _currentSessionId);
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    
                    _logger.LogTrace("Session {SessionId}: Received WebSocket message: Type={MessageType}, Count={Count}, EndOfMessage={EndOfMessage}", 
                        _currentSessionId, result.MessageType, result.Count, result.EndOfMessage);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogTrace("Session {SessionId}: Received JSON: {Json}", _currentSessionId, json);
                        await HandleRealtimeEventAsync(json);
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Session {SessionId}: Realtime API connection closed", _currentSessionId);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Session {SessionId}: Realtime API listener cancelled", _currentSessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Error in Realtime API event listener", _currentSessionId);
            }
              _logger.LogInformation("Session {SessionId}: Stopped listening for Realtime API events", _currentSessionId);
        }

        /// <summary>
        /// Handle events from OpenAI Realtime API
        /// </summary>
        private async Task HandleRealtimeEventAsync(string json)
        {
            try
            {
                _logger.LogInformation("Session {SessionId}: Received event from OpenAI Realtime API: {Json}", _currentSessionId, json);
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    _logger.LogWarning("Session {SessionId}: Event missing 'type' property: {Json}", _currentSessionId, json);
                    return;
                }

                string eventType = typeElement.GetString() ?? "";
                _logger.LogInformation("Session {SessionId}: Processing event type: {EventType}", _currentSessionId, eventType);

                switch (eventType)
                {
                    case "session.created":
                        _logger.LogInformation("Session {SessionId}: Realtime session created", _currentSessionId);
                        break;

                    case "session.updated":
                        _logger.LogInformation("Session {SessionId}: Realtime session configuration updated", _currentSessionId);
                        break;

                    case "input_audio_buffer.speech_started":
                        _logger.LogInformation("Session {SessionId}: Speech started (detected by OpenAI VAD)", _currentSessionId);
                        if (OnSpeechStarted != null && _currentSessionId != null)
                        {
                            await OnSpeechStarted.Invoke(_currentSessionId);
                        }
                        break;

                    case "input_audio_buffer.speech_stopped":
                        _logger.LogInformation("Session {SessionId}: Speech stopped (detected by OpenAI VAD)", _currentSessionId);
                        if (OnSpeechEnded != null && _currentSessionId != null)
                        {
                            await OnSpeechEnded.Invoke(_currentSessionId);
                        }
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                        if (root.TryGetProperty("transcript", out var transcriptElement))
                        {
                            string transcript = transcriptElement.GetString() ?? "";
                            _logger.LogInformation("Session {SessionId}: Transcription completed: '{Transcript}'", _currentSessionId, transcript);
                            
                            if (OnTranscriptionReady != null && _currentSessionId != null)
                            {
                                await OnTranscriptionReady.Invoke(transcript, _currentSessionId, false);
                            }
                        }
                        break;

                    case "error":
                        if (root.TryGetProperty("error", out var errorElement) && 
                            errorElement.TryGetProperty("message", out var messageElement))
                        {
                            string errorMessage = messageElement.GetString() ?? "Unknown error";
                            _logger.LogError("Session {SessionId}: Realtime API error: {Error}", _currentSessionId, errorMessage);
                            
                            if (OnError != null && _currentSessionId != null)
                            {
                                await OnError.Invoke(_currentSessionId, errorMessage);
                            }
                        }
                        break;

                    default:
                        _logger.LogInformation("Session {SessionId}: Unhandled event type '{EventType}': {Json}", _currentSessionId, eventType, json);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session {SessionId}: Error handling Realtime event: {Json}", _currentSessionId, json);
            }
        }

        public void Dispose()
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
    }
}
