using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
                    /// WICHTIG: Implementiert eine konservative Strategie, die niemals mitten im Wort trennt
                    /// </summary>
                    /// <returns>True, wenn der Puffer zum Synthesizer geschickt werden sollte</returns>
                    bool ShouldFlush(StringBuilder buffer, char lastChar)
                    {
                        // Keine Verarbeitung bei leerem Puffer oder sehr kurzem Text (warte auf mehr)
                        if (buffer.Length < 10) return false;

                        // Schneller Pfad: Satzenden sind immer gute Stellen zum Flushen
                        // Aber Vorsicht vor Ellipsen (...) - nicht als Satzende zählen
                        bool isEndOfSentence = ".!?".Contains(lastChar);

                        // Bei Punkten prüfen, ob es sich um eine Ellipse handelt
                        if (lastChar == '.' && buffer.Length >= 3)
                        {
                            // Ist dies Teil einer Ellipse?
                            if (buffer.Length >= 3 &&
                                buffer[buffer.Length - 2] == '.' &&
                                buffer[buffer.Length - 3] == '.')
                            {
                                // Teil einer Ellipse, kein echtes Satzende
                                isEndOfSentence = false;
                            }
                        }

                        // Absätze sind auch gute Stellen zum Flushen
                        bool isParagraphEnd = lastChar == '\n' || lastChar == '\r';

                        // WICHTIG: Nur flushen, wenn wir ein vollständiges Wort haben
                        // Vollständiges Wort: endet mit Interpunktion oder Leerzeichen
                        bool hasCompleteWord = char.IsWhiteSpace(lastChar) ||
                                              char.IsPunctuation(lastChar) ||
                                              lastChar == '\'' || lastChar == '"';

                        // WICHTIG: Bei Kommata prüfen, ob es sich um ein Komma in einer Zahl handelt (z.B. 1,000)
                        if (lastChar == ',' && buffer.Length >= 2)
                        {
                            // Prüfen, ob Ziffern vor und nach dem Komma stehen
                            bool isDigitBefore = char.IsDigit(buffer[buffer.Length - 2]);
                            // Das nächste Zeichen sehen wir noch nicht, also konservativ sein
                            hasCompleteWord = !isDigitBefore; // Wenn keine Ziffer vorher, ist es wahrscheinlich ein echtes Komma
                        }

                        // Sonderbehandlung für Gedankenstriche und andere Satzzeichen:
                        // Nur als Trennstelle betrachten, wenn danach ein Leerzeichen folgt oder danach das Ende ist
                        if (char.IsPunctuation(lastChar) && !".!?,:;".Contains(lastChar))
                        {
                            // Eher keine gute Trennstelle, wenn nicht eines der Hauptsatzzeichen
                            hasCompleteWord = false;
                        }

                        // Größere Länge: Weiche Längengrenze, nur flushen an natürlichen Grenzen
                        // und nur wenn wir garantiert nicht mitten im Wort sind
                        // 
                        // WICHTIG: Wir erhöhen den Schwellwert auf 10, um sicherzustellen, dass
                        // wir genug Text für eine sinnvolle TTS-Verarbeitung haben
                        bool hasReachedSizeThreshold = buffer.Length >= 10 && hasCompleteWord;

                        // Die wichtigste Entscheidung: Vollständiger Satz endet mit . ! ?
                        bool isCompleteSentence = isEndOfSentence && hasCompleteWord;

                        // Priorisiere natürliche Grenzen für das Flushen
                        // 1. Satzenden haben höchste Priorität
                        // 2. Absätze sind ebenfalls gute Trennstellen
                        // 3. Kommas sind akzeptabel, wenn sie zu einem vollständigen Wort gehören
                        // 4. Nur bei Überschreitung einer Größenschwelle UND einem vollständigen Wort flushen
                        return isCompleteSentence ||
                               (isParagraphEnd && hasCompleteWord) ||
                               ((/*lastChar == ',' ||*/ lastChar == ';' || lastChar == ':') && hasCompleteWord && buffer.Length >= 40) ||
                               (hasReachedSizeThreshold && buffer.Length >= 100); // Wir wollen hier strenger sein
                    }


                    bool IsSentenceEndBoundary(string text, int pos)
                    {
                        if (pos < 0 || pos >= text.Length) return false;
                        // Satzzeichen gefolgt von Whitespace oder Textende
                        return Regex.IsMatch(text.Substring(pos, Math.Min(2, text.Length - pos)), "^[.!?](?=\\s|$)");
                    }

                    /// <summary>
                    /// Holt den aktuellen Textpuffer und setzt ihn zurück
                    /// Implementiert einen Lookahead-Mechanismus, der garantiert, dass keine Wörter getrennt werden
                    /// </summary>
                    /// <returns>Textinhalt aus dem Puffer, nie leer wenn ShouldFlush true zurückgegeben hat</returns>
                    string FlushSegmentAtSentenceBoundary(StringBuilder buffer)
                    {
                        string text = buffer.ToString();

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            buffer.Clear();
                            return string.Empty;
                        }

                        // IMPLEMENTIERUNG EINES LOOKAHEAD-MECHANISMUS:
                        // 1. Erst nach Satzgrenzen suchen (höchste Priorität)
                        // 2. Wenn keine Satzgrenze gefunden, nach Wortgrenzen suchen
                        // 3. Niemals mitten im Wort trennen!

                        // Setze einen Ziel-Limit für die Suche (Soft-Limit, nicht hart)
                        int targetLimit = 200; // Ungefährer Zielwert, aber nie erzwungen

                        // 1. SCHRITT: Suche zunächst nach einer Satzgrenze
                        int splitPos = -1;
                        for (int i = Math.Min(text.Length - 1, targetLimit * 2); i >= 0; i--)
                        {
                            if (IsSentenceEndBoundary(text, i))
                            {
                                splitPos = i + 1; // inkl. Satzzeichen
                                break;
                            }
                        }

                        // 2. SCHRITT: Falls keine Satzgrenze gefunden, suche nach einer Wortgrenze bei Komma/Semikolon
                        if (splitPos <= 0)
                        {
                            // Suche nach Komma, Semikolon, Doppelpunkt
                            for (int i = Math.Min(text.Length - 1, targetLimit * 2); i >= 0; i--)
                            {
                                if (i < text.Length && (text[i] == ',' || text[i] == ';' || text[i] == ':'))
                                {
                                    // Prüfe, ob es kein Komma in einer Zahl ist (z.B. 1,000)
                                    if (!(text[i] == ',' && i > 0 && i < text.Length - 1 &&
                                          char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])))
                                    {
                                        splitPos = i + 1; // inkl. Satzzeichen
                                        break;
                                    }
                                }
                            }
                        }

                        // 3. SCHRITT: Falls immer noch nichts gefunden, suche nach Leerzeichen
                        if (splitPos <= 0)
                        {
                            // Suche nach dem letzten Leerzeichen nahe dem Ziel
                            for (int i = Math.Min(text.Length - 1, targetLimit * 2); i >= 0; i--)
                            {
                                if (char.IsWhiteSpace(text[i]))
                                {
                                    splitPos = i + 1; // inkl. Leerzeichen
                                    break;
                                }
                            }
                        }

                        // 4. SCHRITT: WICHTIG! Wenn keine natürliche Trennstelle gefunden wurde,
                        // aber ShouldFlush hat bereits true zurückgegeben, müssen wir trotzdem einen
                        // sinnvollen Chunk zurückgeben (nie leeren String!)
                        if (splitPos <= 0)
                        {
                            // Keine natürliche Grenze gefunden, aber wir müssen trotzdem trennen
                            // Wir verwenden hier die gesamte Länge, falls sie nicht zu lang ist
                            if (text.Length <= 200)
                            {
                                splitPos = text.Length;
                            }
                            else
                            {
                                // Bei sehr langem Text suchen wir nach einem geeigneten Wortende
                                int position = Math.Min(150, text.Length - 1);

                                // Finde das nächste Wortende nach dieser Position
                                while (position < text.Length && !char.IsWhiteSpace(text[position]))
                                {
                                    position++;
                                    // Notfall-Abbruch, falls wir kein Wortende finden
                                    if (position >= text.Length - 1)
                                    {
                                        position = text.Length;
                                        break;
                                    }
                                }

                                splitPos = position;
                            }
                        }

                        // Wenn der gefundene Trennpunkt am Anfang eines Wortes ist,
                        // stellen wir sicher, dass wir nicht mitten im Wort trennen
                        if (splitPos > 0 && splitPos < text.Length)
                        {
                            // Falls wir in einem Wort sind, springen wir zum Wortende
                            if (char.IsLetterOrDigit(text[splitPos]) && !char.IsWhiteSpace(text[splitPos - 1]))
                            {
                                // Nach dem Wortende suchen
                                while (splitPos < text.Length && !char.IsWhiteSpace(text[splitPos]) &&
                                       !char.IsPunctuation(text[splitPos]))
                                {
                                    splitPos++;
                                }
                            }
                        }

                        // Stelle sicher, dass der splitPos gültig ist
                        splitPos = Math.Max(1, Math.Min(splitPos, text.Length));

                        // Text bis zum Trennpunkt zurückgeben und Rest im Puffer behalten
                        string flush = text.Substring(0, splitPos).TrimEnd();
                        string rest = splitPos < text.Length ? text.Substring(splitPos).TrimStart() : string.Empty;

                        buffer.Clear(); // Puffer leeren
                        if (!string.IsNullOrEmpty(rest))
                        {
                            buffer.Append(rest); // Rest wieder anhängen
                        }

                        _logger.LogInformation("[LOOKAHEAD-DEBUG] Flushing text chunk at boundary: '{Text}' (Rest: '{Rest}')", flush, rest);

                        return flush;
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
                        // WICHTIG: Niemals leere Chunks verarbeiten oder Chunks mit weniger als 5 Zeichen
                        // Das würde nur zu sinnlosen API-Aufrufen führen
                        if (string.IsNullOrWhiteSpace(textChunk) || textChunk.Trim().Length < 5)
                        {
                            _logger.LogWarning("[TTS-DEBUG] Skipping empty or too short chunk #{Index}", chunkIndex);
                            return;
                        }

                        _logger.LogInformation("[TTS-DEBUG] TTS starting for chunk #{Index}: '{TextChunk}'",
                            chunkIndex, textChunk);

                        try
                        {
                            // TTS-Anfrage starten - verwende das bereinigte Textchunk
                            string cleanedChunk = textChunk.Trim();
                            var audioBytes = await _synthesizer.SynthesizeTextChunkAsync(cleanedChunk, voice);
                            //var audioBytes = await _synthesizer.ChunkedSynthesisAsync(cleanedChunk, voice);

                            // Füge den Chunk zur richtigen Position in der Queue hinzu
                            lock (audioChunkLock)
                            {
                                audioChunks[chunkIndex] = audioBytes;
                            }

                            // Signalisiere, dass ein neuer Chunk bereit ist
                            audioChunkReady.Release();

                            _logger.LogInformation("[TTS-DEBUG] TTS completed for chunk #{Index}: '{TextChunk}' ({Size} bytes)",
                                chunkIndex, textChunk, audioBytes.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error processing TTS for chunk #{Index}: {Error}",
                                chunkIndex, ex.Message);
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
                                    // Jetzt die Flush-Methode aufrufen, die IMMER einen nicht-leeren String zurückgibt
                                    // wenn ShouldFlush() true zurückgegeben hat
                                    string textChunk = FlushSegmentAtSentenceBoundary(sb);

                                    // Nur verarbeiten, wenn wir einen nicht-leeren Chunk haben
                                    if (!string.IsNullOrWhiteSpace(textChunk))
                                    {
                                        int chunkIndex = currentChunkIndex++;

                                        // Chunk-Generierung loggen
                                        var textPreview = textChunk.Length <= 30 ? textChunk : textChunk.Substring(0, 30) + "...";
                                        _logger.LogInformation("[CHUNK-DEBUG] Generated chunk #{Index}: '{Text}'",
                                            chunkIndex, textPreview);

                                        // Starte einen neuen TTS-Task für diesen Chunk
                                        var task = Task.Run(() => StartTtsTaskAsync(textChunk, chunkIndex));
                                        ttsTasks.Add(task);
                                    }
                                    else
                                    {
                                        // Für Debug-Zwecke loggen, dass kein Chunk erzeugt wurde
                                        _logger.LogWarning("[CHUNK-DEBUG] No chunk generated despite ShouldFlush returning true");
                                    }
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
                            // Verarbeite verbleibenden Text, aber nur wenn er lang genug ist
                            string remainingText = sb.ToString().Trim();
                            sb.Clear();

                            if (!string.IsNullOrWhiteSpace(remainingText) && remainingText.Length >= 5)
                            {
                                _logger.LogInformation("[FINAL-CHUNK-DEBUG] Processing remaining text: {TextChunk}",
                                    remainingText.Length <= 30 ? remainingText : remainingText.Substring(0, 30) + "...");

                                // Starte einen TTS-Task für den letzten Chunk
                                int finalChunkIndex = currentChunkIndex++;
                                var task = Task.Run(() => StartTtsTaskAsync(remainingText, finalChunkIndex));
                                ttsTasks.Add(task);
                            }
                            else
                            {
                                _logger.LogInformation("[FINAL-CHUNK-DEBUG] Remaining text too short, skipping: '{Text}'",
                                    remainingText);
                            }
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