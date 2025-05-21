using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoiceAssistant.Core.Interfaces;
using VoiceAssistant.Core.Models;
using VoiceAssistant.Core.Services;
using VoiceAssistant.Plugins.OpenAI;
using WebRtcVadSharp;

namespace VoiceAssistant
{
    /// <summary>
    /// Service for handling WebSocket-based audio streaming with robust VAD segmentation.
    /// </summary>
    public class WebSocketAudioService
    {
        private readonly PipelineOptions _pipelineOptions;
        private readonly IRecognizer _recognizer;
        private readonly IChatService _chatService;
        private readonly ChatLogManager _chatLogManager;
        private readonly ILogger<WebSocketAudioService> _logger;
        private readonly ISynthesizer _synthesizer;
        private readonly WebRtcVad _vad;
        private readonly VadSettings _settings;

        private const int SampleRate = 16000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;
        private const int FrameDurationMs = 20;
        private readonly int _frameBytes;

        // Noise floor estimation
        private double _noiseFloor;
        private double _silenceDurationSec = 0;

        // TTS voice identifier from pipeline options; no internal storage

        public WebSocketAudioService(
            IRecognizer recognizer,
            IChatService chatService,
            ChatLogManager chatLogManager,
            ISynthesizer synthesizer,
            ILogger<WebSocketAudioService> logger,
            VadSettings settings,
            PipelineOptions pipelineOptions)
        {
            _recognizer = recognizer;
            _chatService = chatService;
            _chatLogManager = chatLogManager;
            _synthesizer = synthesizer;
            _logger = logger;
            _settings = settings;
            _pipelineOptions = pipelineOptions;

            _vad = new WebRtcVad
            {
                OperatingMode = _settings.OperatingMode,
                SampleRate = WebRtcVadSharp.SampleRate.Is16kHz,
                FrameLength = FrameLength.Is20ms
            };

            _frameBytes = SampleRate * Channels * BitsPerSample / 8 * FrameDurationMs / 1000;

            // Initialize noise floor via short calibration window
            _noiseFloor = MeasureInitialNoiseFloor();
        }

        private double MeasureInitialNoiseFloor()
        {
            // Implement a short capture of ambient noise (e.g., 1 second) to set MinNoiseFloor
            // For simplicity, use MinNoiseFloor as initial value
            return _settings.MinNoiseFloor;
        }

        public async Task HandleAsync(WebSocket webSocket)
        {
            _logger.LogInformation("WebSocket /ws/audio connected");
            _logger.LogInformation(
                "VAD Settings: Mode={Mode}, PreAmp={PreAmp:F1}, MinSpeech={MinSpeech:F2}s, PreSpeech={PreSpeech:F2}s, Hangover={Hangover:F2}s",
                _settings.OperatingMode,
                _settings.PreAmplification,
                _settings.MinSpeechDurationSec,
                _settings.PreSpeechDurationSec,
                _settings.HangoverDurationSec);

            var rawAudio = new List<byte>();
            var buffer = new byte[_frameBytes];
            int preFrames = (int)(_settings.PreSpeechDurationSec * 1000 / FrameDurationMs);
            int startFrames = (int)(_settings.MinSpeechDurationSec * 1000 / FrameDurationMs);
            int endFrames = (int)(_settings.HangoverDurationSec * 1000 / FrameDurationMs);

            var preBuffer = new Queue<byte[]>();
            var segmentBuffer = new List<byte>();
            bool inSpeech = false;
            int consecSpeech = 0;
            int consecSilence = 0;

            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (segmentBuffer.Count > 0)
                        await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket);
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary || result.Count != _frameBytes)
                    continue;

                // Copy frame
                var frame = new byte[_frameBytes];
                Array.Copy(buffer, frame, _frameBytes);
                rawAudio.AddRange(frame);

                if (_pipelineOptions.DisableVad) continue;

                // Pre-amplify
                ApplyPreAmplification(frame);

                // Calculate per-frame RMS
                double frameRms = CalculateRms(frame);

                // Run VAD
                bool hasSpeech = _vad.HasSpeech(frame);

                // Track silence duration
                if (!hasSpeech)
                    _silenceDurationSec += FrameDurationMs / 1000.0;
                else
                    _silenceDurationSec = 0;

                // Update noise floor only after sustained silence
                if (!hasSpeech && _silenceDurationSec >= _settings.SilenceAdaptationTimeSec)
                {
                    _noiseFloor = Math.Max(_settings.MinNoiseFloor,
                        _settings.NoiseFloorAlpha * _noiseFloor + (1 - _settings.NoiseFloorAlpha) * frameRms);
                }

                // Compute dynamic threshold
                double dynamicThreshold = Math.Max(_settings.MinNoiseFloor,
                    _noiseFloor * _settings.NoiseThresholdFactor);

                // Combined decision
                bool isSpeech = hasSpeech && frameRms >= dynamicThreshold;

                // Pre-roll
                preBuffer.Enqueue(frame);
                if (preBuffer.Count > preFrames)
                    preBuffer.Dequeue();

                if (!inSpeech)
                {
                    if (isSpeech && ++consecSpeech >= startFrames)
                    {
                        inSpeech = true;
                        consecSilence = 0;
                        segmentBuffer.Clear();
                        foreach (var buf in preBuffer) segmentBuffer.AddRange(buf);
                        _logger.LogInformation("VAD: Speech started");
                    }
                    else if (!isSpeech)
                    {
                        consecSpeech = 0;
                    }
                }
                else
                {
                    segmentBuffer.AddRange(frame);
                    if (!isSpeech && ++consecSilence >= endFrames)
                    {
                        inSpeech = false;
                        _logger.LogInformation("VAD: Speech ended ({Bytes} bytes)", segmentBuffer.Count);
                        await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket);
                        segmentBuffer.Clear();
                        consecSpeech = consecSilence = 0;
                    }
                    else if (isSpeech)
                    {
                        consecSilence = 0;
                    }
                }
            }
        }

        private void ApplyPreAmplification(byte[] frame)
        {
            if (_settings.PreAmplification == 1.0f) return;
            for (int i = 0; i < frame.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(frame, i);
                int amplified = (int)(sample * _settings.PreAmplification);
                amplified = Math.Clamp(amplified, short.MinValue, short.MaxValue);
                var bytes = BitConverter.GetBytes((short)amplified);
                frame[i] = bytes[0];
                frame[i + 1] = bytes[1];
            }
        }

        private static double CalculateRms(byte[] frame)
        {
            double sum = 0;
            int count = frame.Length / 2;
            for (int i = 0; i < frame.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(frame, i);
                sum += sample * sample;
            }
            return Math.Sqrt(sum / count) / short.MaxValue;
        }

        private byte[] CreateWavHeader(int dataLength)
        {
            int byteRate = SampleRate * Channels * BitsPerSample / 8;
            short blockAlign = (short)(Channels * BitsPerSample / 8);
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)Channels);
            writer.Write(SampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write((short)BitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            writer.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// Verarbeitet ein erkanntes Audiosegment mit echter End-to-End-Streaming-Pipeline.
        /// Implementiert paralleles Token-Streaming und TTS-Streaming für minimale Latenz.
        /// </summary>
        /// <param name="audioBytes">Rohes Audiosegment (PCM-Daten vom VAD)</param>
        /// <param name="webSocket">WebSocket-Verbindung für Event-Streaming</param>
        private async Task ProcessSegmentAsync(byte[] audioBytes, WebSocket webSocket)
        {
            double durationSec = (double)audioBytes.Length / (SampleRate * Channels * BitsPerSample / 8);
            if (durationSec < _settings.MinSegmentDurationSec)
            {
                _logger.LogInformation("Segment verworfen: Dauer {Duration:F3}s < Min {MinSec:F3}s", durationSec, _settings.MinSegmentDurationSec);
                return;
            }
            try
            {
                _logger.LogInformation("Processing segment: {Bytes} bytes", audioBytes.Length);
                using var ms = new MemoryStream();
                var header = CreateWavHeader(audioBytes.Length);
                ms.Write(header, 0, header.Length);
                ms.Write(audioBytes, 0, audioBytes.Length);
                ms.Position = 0;

                string prompt = await _recognizer.RecognizeAsync(ms, "audio/wav", "segment.wav");
                _logger.LogInformation("Transcription: '{Prompt}'", prompt);

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                string reply;
                var voice = _pipelineOptions.TtsVoice;

                if (!_pipelineOptions.DisableTokenStreaming && _chatService is StreamingOpenAIChatService)
                {
                    // ---- ECHTES END-TO-END-STREAMING ----
                    // Diese Implementation parallelisiert das Token-Streaming und TTS-Streaming,
                    // sodass Audio schon während der Text-Generierung erzeugt und abgespielt werden kann

                    var streaming = (StreamingOpenAIChatService)_chatService;
                    var sb = new StringBuilder();

                    // Hilfsfunktionen zum Verarbeiten von Teiltext für TTS

                    /// <summary>
                    /// Bestimmt, ob der aktuelle Textpuffer für die TTS-Verarbeitung bereit ist
                    /// </summary>
                    /// <returns>True, wenn der Puffer zum Synthesizer geschickt werden sollte</returns>
                    bool ShouldFlush(StringBuilder buffer, char lastChar)
                    {
                        // Satzenden und Punkte sind gute Stellen zum Flushen
                        bool isEndOfSentence = ".!?:;".Contains(lastChar);

                        // Flush bei Erreichen einer Mindestlänge oder bei Satzenden
                        return buffer.Length >= 50 || (buffer.Length >= 20 && isEndOfSentence);
                    }

                    /// <summary>
                    /// Holt den aktuellen Textpuffer und setzt ihn zurück
                    /// </summary>
                    /// <returns>Textinhalt aus dem Puffer</returns>
                    string FlushSegment(StringBuilder buffer)
                    {
                        string segment = buffer.ToString();
                        buffer.Clear();
                        return segment;
                    }

                    // Queue für das Tracking der TTS-Verarbeitungs-Tasks, um die richtige Reihenfolge beim Abspielen sicherzustellen
                    var ttsTaskQueue = new System.Collections.Concurrent.ConcurrentQueue<(Task<byte[]> Task, string TextChunk)>();

                    // Semaphore für das sequentielle Senden von Audio-Chunks (nicht für die TTS-Verarbeitung!)
                    SemaphoreSlim audioSendSemaphore = new SemaphoreSlim(1, 1);

                    // Audio-Queue-System für geordnete Verarbeitung
                    int nextChunkIndex = 0;  // Der nächste zu sendende Chunk-Index
                    var audioChunks = new Dictionary<int, byte[]>();  // Speichert fertige Audio-Chunks nach Position
                    var audioChunkReady = new SemaphoreSlim(0);  // Signalisiert, wenn neue Chunks bereit sind
                    var audioChunkLock = new object();  // Lock für Thread-Sicherheit
                    bool isResponseComplete = false;  // Flag für "Alle TTS-Aufgaben sind abgeschlossen"

                    // Task zur sequentiellen Verarbeitung der fertigen Audio-Chunks
                    Task audioProcessingTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                // Warte auf Signal, dass ein neuer Chunk bereit ist
                                await audioChunkReady.WaitAsync();

                                // Sende alle verfügbaren Chunks in der richtigen Reihenfolge
                                bool sentAny = false;

                                do
                                {
                                    sentAny = false;
                                    byte[] chunk = null;

                                    // Prüfe ob der nächste Chunk in der Reihenfolge bereit ist
                                    lock (audioChunkLock)
                                    {
                                        if (audioChunks.TryGetValue(nextChunkIndex, out chunk))
                                        {
                                            audioChunks.Remove(nextChunkIndex);
                                            nextChunkIndex++;
                                            sentAny = true;
                                        }
                                    }

                                    // Wenn ein Chunk bereit ist, sende ihn mit Index
                                    if (sentAny && chunk != null)
                                    {
                                        await SendEventAsync(webSocket, "audio-chunk",
                                            new
                                            {
                                                chunk = Convert.ToBase64String(chunk),
                                                index = nextChunkIndex - 1  // Sende Chunk-Index mit
                                            });

                                        int sentIndex = nextChunkIndex - 1;
                                        _logger.LogInformation("[WEBSOCKET-DEBUG] Sent audio chunk #{Index} ({Size} bytes)",
                                            sentIndex, chunk.Length);
                                    }
                                } while (sentAny);  // Solange Chunks verfügbar sind, weitermachen

                                // Prüfe, ob wir fertig sind
                                lock (audioChunkLock)
                                {
                                    if (isResponseComplete && audioChunks.Count == 0)
                                    {
                                        break;  // Alle Chunks wurden gesendet, fertig
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error in audio processing task");
                        }
                    });

                    // Hilfsfunktion zum Starten einer TTS-Anfrage und Hinzufügen zur Queue
                    async Task StartTtsTaskAsync(string textChunk, int chunkIndex)
                    {
                        if (string.IsNullOrWhiteSpace(textChunk))
                            return;

                        var textPreview = textChunk.Length <= 50 ? textChunk : textChunk.Substring(0, 50) + "...";
                        _logger.LogInformation("[TTS-DEBUG] TTS starting for chunk #{Index}: '{TextChunk}'",
                            chunkIndex, textPreview);

                        try
                        {
                            // TTS-Anfrage starten
                            var audioBytes = await _synthesizer.SynthesizeTextChunkAsync(textChunk, voice);

                            // Füge den Chunk zur richtigen Position in der Queue hinzu
                            lock (audioChunkLock)
                            {
                                audioChunks[chunkIndex] = audioBytes;
                            }

                            // Signalisiere, dass ein neuer Chunk bereit ist
                            audioChunkReady.Release();

                            _logger.LogInformation("[TTS-DEBUG] TTS completed for chunk #{Index}: '{TextChunk}' ({Size} bytes)",
                                chunkIndex, textPreview, audioBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing TTS for chunk #{Index}", chunkIndex);
                        }
                    }

                    // Generiere Antwort mit Token-Callback für Parallelisierung
                    // Der Callback wird für jedes ankommende Token ausgeführt und startet
                    // bei passenden Stellen (Satzenden, ausreichende Länge) eine TTS-Anfrage
                    int currentChunkIndex = 0;  // Zählt die Chunks für die richtige Reihenfolge
                    List<Task> ttsTasks = new List<Task>();  // Liste für alle TTS-Tasks

                    reply = await streaming.GenerateStreamingResponseAsync(
                        _chatLogManager.GetMessages(),
                        async token =>
                        {
                            try
                            {
                                // 1. Token zum Frontend senden für sofortige Text-Anzeige
                                await SendEventAsync(webSocket, "token", new { token });

                                // 2. Token im Buffer für spätere TTS-Verarbeitung sammeln
                                sb.Append(token);

                                // 3. Bei ausreichender Größe oder Satzende TTS-Synthese starten
                                // Nur starten, wenn ein nicht-leeres Token das Entscheidungskriterium auslöst
                                if (token.Length > 0 && ShouldFlush(sb, token[token.Length - 1]))
                                {
                                    string textChunk = FlushSegment(sb);
                                    int chunkIndex = currentChunkIndex++;

                                    // Chunk-Generierung loggen
                                    var textPreview = textChunk.Length <= 30 ? textChunk : textChunk.Substring(0, 30) + "...";
                                    _logger.LogInformation("[CHUNK-DEBUG] Generated chunk #{Index}: '{Text}'",
                                        chunkIndex, textPreview);

                                    // Starte einen neuen TTS-Task für diesen Chunk
                                    var task = Task.Run(() => StartTtsTaskAsync(textChunk, chunkIndex));
                                    ttsTasks.Add(task);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error processing token in streaming response");
                            }
                        });

                    // Restlichen Text verarbeiten, falls vorhanden
                    if (sb.Length > 0)
                    {
                        try
                        {
                            // Verarbeite verbleibenden Text
                            string remainingText = sb.ToString();
                            sb.Clear();

                            _logger.LogDebug("Processing remaining text: {TextChunk}",
                                remainingText.Length <= 30 ? remainingText : remainingText.Substring(0, 30) + "...");

                            // Starte einen TTS-Task für den letzten Chunk
                            int finalChunkIndex = currentChunkIndex;
                            var task = Task.Run(() => StartTtsTaskAsync(remainingText, finalChunkIndex));
                            ttsTasks.Add(task);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing remaining text");
                        }
                    }

                    // Auf Abschluss aller TTS-Verarbeitungen warten
                    _logger.LogDebug("Waiting for {Count} TTS tasks to complete", ttsTasks.Count);
                    await Task.WhenAll(ttsTasks);

                    // Warte auf Abschluss des Audio-Verarbeitungs-Tasks
                    _logger.LogDebug("Waiting for audio processing task to complete");

                    // Setze ein Flag für die Fertigstellung und sende restliche Chunks
                    lock (audioChunkLock)
                    {
                        isResponseComplete = true;
                        audioChunkReady.Release();  // Signal senden, dass wir fertig sind
                    }

                    await audioProcessingTask;
                }
                else
                {
                    // Fallback für nicht-streaming Modus
                    reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
                    await SendEventAsync(webSocket, "token", new { token = reply });

                    // TTS wie bisher
                    _logger.LogInformation("Using TTS voice: {Voice}", voice);
                    var audioOut = await _synthesizer.SynthesizeAsync(reply, voice);
                    await SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(audioOut) });
                }

                // Annotate chat log entry with current pipeline settings
                var botMsg = _chatLogManager.AddMessage(
                    ChatRole.Bot,
                    reply,
                    _pipelineOptions.ChatModel,
                    _pipelineOptions.TtsVoice);
                _logger.LogInformation("Reply: '{Reply}'", reply);

                await SendEventAsync(webSocket, "audio-done", null);
                await SendEventAsync(webSocket, "done", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing segment");
                await SendEventAsync(webSocket, "error", new { error = ex.Message });
            }
        }

        private static async Task SendEventAsync(WebSocket webSocket, string eventName, object data)
        {
            var payload = JsonSerializer.Serialize(new { @event = eventName, data });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}