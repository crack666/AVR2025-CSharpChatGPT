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
            bool potentialSpikeDetected = false; // Flag for spike detection
            bool speechPrimedBySpike = false; // Indicates if speech was initiated by a spike

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

                // --- BEGIN Spike Detection Logic ---
                // A spike is a strong, sudden increase in energy.
                // It can prime the VAD to start speech even if WebRTC VAD is momentarily negative.
                if (!inSpeech && frameRms > _settings.VadSpikeThreshold && frameRms > _noiseFloor * _settings.NoiseThresholdFactor * 1.5) // Spike must also be significantly above noise floor
                {
                    potentialSpikeDetected = true;
                    _logger.LogInformation("VAD: Potential spike detected. RMS: {FrameRms:F4}, NoiseFloor: {NoiseFloor:F4}", frameRms, _noiseFloor);
                }
                // --- END Spike Detection Logic ---

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

                // Combined decision - incorporating spike detection
                // A spike can trigger 'isSpeech' even if WebRTC VAD is momentarily negative,
                // but still require RMS to be above the dynamic threshold to avoid noise spikes.
                bool isWebRtcSpeech = hasSpeech; // Store original WebRTC VAD result
                bool isRmsAboveThreshold = frameRms >= dynamicThreshold;
                
                // Core speech detection: either WebRTC VAD says speech AND RMS is above threshold,
                // OR a spike was detected AND RMS is above threshold.
                bool activeSpeechSignal = (isWebRtcSpeech && isRmsAboveThreshold) || (potentialSpikeDetected && isRmsAboveThreshold);


                // Pre-roll
                preBuffer.Enqueue(frame);
                if (preBuffer.Count > preFrames)
                    preBuffer.Dequeue();

                if (!inSpeech)
                {
                    // Incorporate potentialSpikeDetected into the start condition
                    if (activeSpeechSignal)
                    {
                        consecSpeech++;
                        if (potentialSpikeDetected) speechPrimedBySpike = true; // Mark that a spike contributed

                        if (consecSpeech >= startFrames || speechPrimedBySpike) // If primed by spike, start sooner
                        {
                            inSpeech = true;
                            // If started by spike, ensure min speech duration is met by effectively setting consecSpeech high.
                            // Otherwise, use the actual consecutive speech frames.
                            consecSpeech = speechPrimedBySpike ? startFrames : consecSpeech; 
                            consecSilence = 0;
                            segmentBuffer.Clear();
                            foreach (var buf in preBuffer) segmentBuffer.AddRange(buf);
                            // Add current frame that triggered speech, as preBuffer might not have it if preFrames is 0
                            if (!preBuffer.Contains(frame)) segmentBuffer.AddRange(frame);

                            _logger.LogInformation("VAD: Speech started (Spike: {IsSpikeTriggered}, ConsecFrames: {ConsecSpeech}, RMS: {FrameRms:F4}, DynThr: {DynThr:F4})",
                                                 speechPrimedBySpike, consecSpeech, frameRms, dynamicThreshold);
                            potentialSpikeDetected = false; 
                            speechPrimedBySpike = false; // Reset spike priming flag
                        }
                    }
                    else
                    {
                        consecSpeech = 0;
                        potentialSpikeDetected = false; // Reset if no qualifying speech signal follows spike
                        speechPrimedBySpike = false;
                    }
                }
                else // inSpeech == true
                {
                    segmentBuffer.AddRange(frame);
                    // Use activeSpeechSignal for hangover logic as well
                    if (!activeSpeechSignal && ++consecSilence >= endFrames)
                    {
                        inSpeech = false;
                        _logger.LogInformation("VAD: Speech ended ({Bytes} bytes, ConsecSilence: {ConsecSilence})", segmentBuffer.Count, consecSilence);
                        await ProcessSegmentAsync(segmentBuffer.ToArray(), webSocket);
                        segmentBuffer.Clear();
                        consecSpeech = consecSilence = 0;
                        potentialSpikeDetected = false; 
                        speechPrimedBySpike = false;
                    }
                    else if (activeSpeechSignal)
                    {
                        consecSilence = 0;
                        // Reset potentialSpikeDetected if speech continues, as it's no longer a "potential" start spike.
                        potentialSpikeDetected = false; 
                        speechPrimedBySpike = false;
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

                MemoryStream audioMemoryStream = PrepareAudioStreamForTranscription(audioBytes);
                string prompt = await GetTranscriptionAsync(audioMemoryStream);

                _chatLogManager.AddMessage(ChatRole.User, prompt);
                await SendEventAsync(webSocket, "prompt", new { prompt });

                string reply;
                if (!_pipelineOptions.DisableTokenStreaming && _chatService is StreamingOpenAIChatService streamingChatService)
                {
                    reply = await HandleStreamingChatResponseAsync(webSocket, streamingChatService, prompt);
                }
                else
                {
                    reply = await HandleNonStreamingChatResponseAsync(webSocket, prompt);
                }

                LogAndSendFinalEvents(webSocket, reply);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing segment");
                await SendEventAsync(webSocket, "error", new { error = ex.Message });
            }
        }

        private MemoryStream PrepareAudioStreamForTranscription(byte[] audioBytes)
        {
            var ms = new MemoryStream();
            var header = CreateWavHeader(audioBytes.Length);
            ms.Write(header, 0, header.Length);
            ms.Write(audioBytes, 0, audioBytes.Length);
            ms.Position = 0;
            return ms;
        }

        private async Task<string> GetTranscriptionAsync(MemoryStream audioMemoryStream)
        {
            string prompt = await _recognizer.RecognizeAsync(audioMemoryStream, "audio/wav", "segment.wav");
            _logger.LogInformation("Transcription: '{Prompt}'", prompt);
            return prompt;
        }

        private async Task<string> HandleStreamingChatResponseAsync(WebSocket webSocket, StreamingOpenAIChatService streamingChatService, string prompt)
        {
            var voice = _pipelineOptions.TtsVoice;
            var sb = new StringBuilder();
            string fullReply = string.Empty; // To accumulate the full reply for logging

            // Local functions ShouldFlush, FlushSegmentAtSentenceBoundary, IsSentenceEndBoundary, StartTtsTaskAsync remain here
            // ... (definitions of ShouldFlush, FlushSegmentAtSentenceBoundary, IsSentenceEndBoundary as they were)
            bool ShouldFlush(StringBuilder buffer, char lastChar, bool isFirstChunkSent)
            {
                // MODIFIED: Different logic for first chunk
                if (!isFirstChunkSent)
                {
                    // For the first chunk, flush more aggressively: at sentence end or if a certain length is reached.
                    bool currentIsPotentialEndOfFirstChunk = buffer.Length >= _pipelineOptions.TtsMinFirstChunkLength; 
                    bool currentIsEndOfSentence = ".!?".Contains(lastChar);
                    if (lastChar == '.' && buffer.Length >= 3 && buffer[buffer.Length - 2] == '.' && buffer[buffer.Length - 3] == '.') currentIsEndOfSentence = false;
                    bool currentHasCompleteWord = char.IsWhiteSpace(lastChar) || char.IsPunctuation(lastChar) || lastChar == '\'' || lastChar == '"';
                    
                    return (currentIsEndOfSentence && currentHasCompleteWord && buffer.Length > 0) || (currentIsPotentialEndOfFirstChunk && currentHasCompleteWord);
                }

                // Logic for subsequent chunks (can be refined further based on _pipelineOptions.TtsSubsequentChunkLength)
                if (buffer.Length < _pipelineOptions.TtsSubsequentChunkLength / 4 && buffer.Length < 10) return false; // Avoid very short chunks for subsequent parts

                bool isEndOfSentence = ".!?".Contains(lastChar);
                if (lastChar == '.' && buffer.Length >= 3 && buffer[buffer.Length - 2] == '.' && buffer[buffer.Length - 3] == '.')
                {
                    isEndOfSentence = false;
                }

                bool isParagraphEnd = lastChar == '\n' || lastChar == '\r';

                bool hasCompleteWord = char.IsWhiteSpace(lastChar) || char.IsPunctuation(lastChar) || lastChar == '\'' || lastChar == '"';

                if (lastChar == ',' && buffer.Length >= 2)
                {
                    bool isDigitBefore = char.IsDigit(buffer[buffer.Length - 2]);
                    hasCompleteWord = !isDigitBefore; 
                }

                if (char.IsPunctuation(lastChar) && !".!?,:;".Contains(lastChar))
                {
                    hasCompleteWord = false;
                }
                
                bool hasReachedSubsequentChunkLength = buffer.Length >= _pipelineOptions.TtsSubsequentChunkLength && hasCompleteWord;

                bool isCompleteSentence = isEndOfSentence && hasCompleteWord;

                return isCompleteSentence ||
                       (isParagraphEnd && hasCompleteWord) ||
                       ((lastChar == ';' || lastChar == ':') && hasCompleteWord && buffer.Length >= _pipelineOptions.TtsSubsequentChunkLength / 2) || 
                       hasReachedSubsequentChunkLength;
            }

            bool IsSentenceEndBoundary(string text, int pos)
            {
                if (pos < 0 || pos >= text.Length) return false;
                return Regex.IsMatch(text.Substring(pos, Math.Min(2, text.Length - pos)), "^[.!?](?=\\s|$)");
            }

            string FlushSegmentAtSentenceBoundary(StringBuilder buffer, bool isFirstChunkSent)
            {
                string currentText = buffer.ToString(); 

                if (string.IsNullOrWhiteSpace(currentText))
                {
                    buffer.Clear();
                    return string.Empty;
                }

                if (!isFirstChunkSent)
                {
                    int firstChunkTargetLimit = _pipelineOptions.TtsMaxFirstChunkLength; 
                    int currentSplitPos = -1; 
                    if (currentText.Length <= firstChunkTargetLimit) {
                        currentSplitPos = currentText.Length;
                    } else {
                        for (int i = Math.Min(currentText.Length - 1, firstChunkTargetLimit); i >= 0; i--)
                        {
                            if (IsSentenceEndBoundary(currentText, i))
                            {
                                currentSplitPos = i + 1; 
                                break;
                            }
                        }
                        if (currentSplitPos <= 0) { 
                            for (int i = Math.Min(currentText.Length - 1, firstChunkTargetLimit); i >=0; i--) {
                                if (char.IsWhiteSpace(currentText[i]) || char.IsPunctuation(currentText[i])) {
                                    currentSplitPos = i + 1;
                                    break;
                                }
                            }
                        }
                        if (currentSplitPos <= 0) currentSplitPos = Math.Min(currentText.Length, firstChunkTargetLimit); 
                    }
                    string flushSegment = currentText.Substring(0, currentSplitPos).TrimEnd(); 
                    string remainingText = currentSplitPos < currentText.Length ? currentText.Substring(currentSplitPos).TrimStart() : string.Empty; 
                    buffer.Clear();
                    if (!string.IsNullOrEmpty(remainingText)) buffer.Append(remainingText);
                    _logger.LogInformation("[LOOKAHEAD-DEBUG] Flushing FIRST text chunk: '{Text}' (Rest: '{Rest}')", flushSegment, remainingText);
                    return flushSegment;
                }

                string originalText = buffer.ToString();
                int originalSplitPos = -1;
                int targetLimit = _pipelineOptions.TtsSubsequentChunkLength; 

                for (int i = Math.Min(originalText.Length - 1, targetLimit * 2); i >= 0; i--)
                {
                    if (IsSentenceEndBoundary(originalText, i))
                    {
                        originalSplitPos = i + 1;
                        break;
                    }
                }

                if (originalSplitPos <= 0)
                {
                    for (int i = Math.Min(originalText.Length - 1, targetLimit * 2); i >= 0; i--)
                    {
                        if (i < originalText.Length && (originalText[i] == ',' || originalText[i] == ';' || originalText[i] == ':'))
                        {
                            if (!(originalText[i] == ',' && i > 0 && i < originalText.Length - 1 &&
                                  char.IsDigit(originalText[i - 1]) && char.IsDigit(originalText[i + 1])))
                            {
                                originalSplitPos = i + 1;
                                break;
                            }
                        }
                    }
                }

                if (originalSplitPos <= 0)
                {
                    for (int i = Math.Min(originalText.Length - 1, targetLimit * 2); i >= 0; i--)
                    {
                        if (char.IsWhiteSpace(originalText[i]))
                        {
                            originalSplitPos = i + 1;
                            break;
                        }
                    }
                }

                if (originalSplitPos <= 0)
                {
                    if (originalText.Length <= targetLimit) 
                    {
                        originalSplitPos = originalText.Length;
                    }
                    else
                    {
                        int position = Math.Min(Math.Max(20, (int)(targetLimit * 0.75)), originalText.Length - 1);
                        while (position < originalText.Length && !char.IsWhiteSpace(originalText[position]))
                        {
                            position++;
                            if (position >= originalText.Length - 1)
                            {
                                position = originalText.Length;
                                break;
                            }
                        }
                        originalSplitPos = position;
                    }
                }

                if (originalSplitPos > 0 && originalSplitPos < originalText.Length)
                {
                    if (char.IsLetterOrDigit(originalText[originalSplitPos]) && !char.IsWhiteSpace(originalText[originalSplitPos - 1]))
                    {
                        while (originalSplitPos < originalText.Length && !char.IsWhiteSpace(originalText[originalSplitPos]) &&
                               !char.IsPunctuation(originalText[originalSplitPos]))
                        {
                            originalSplitPos++;
                        }
                    }
                }
                originalSplitPos = Math.Max(1, Math.Min(originalSplitPos, originalText.Length));
                string finalFlushSegment = originalText.Substring(0, originalSplitPos).TrimEnd();
                string finalRemainingText = originalSplitPos < originalText.Length ? originalText.Substring(originalSplitPos).TrimStart() : string.Empty;
                buffer.Clear();
                if (!string.IsNullOrEmpty(finalRemainingText)) buffer.Append(finalRemainingText);
                _logger.LogInformation("[LOOKAHEAD-DEBUG] Flushing text chunk at boundary: '{Text}' (Rest: '{Rest}')", finalFlushSegment, finalRemainingText);
                return finalFlushSegment;
            }

            var ttsTaskQueue = new System.Collections.Concurrent.ConcurrentQueue<(Task<byte[]> Task, string TextChunk)>();
            SemaphoreSlim audioSendSemaphore = new SemaphoreSlim(1, 1);
            int nextChunkIndex = 0;
            var audioChunks = new Dictionary<int, byte[]>();
            var audioChunkReady = new SemaphoreSlim(0);
            var audioChunkLock = new object();
            bool isResponseComplete = false;

            async Task StartTtsTaskAsync(string textChunk, int chunkIndex, bool isFirstChunk)
            {
                if (string.IsNullOrWhiteSpace(textChunk) || textChunk.Trim().Length < 5)
                {
                    _logger.LogWarning("[TTS-DEBUG] Skipping empty or too short chunk #{Index}", chunkIndex);
                    return;
                }
                _logger.LogInformation("[TTS-DEBUG] TTS starting for chunk #{Index}: '{TextChunk}'", chunkIndex, textChunk);
                try
                {
                    string cleanedChunk = textChunk.Trim();
                    byte[] audioData;
                    if (isFirstChunk)
                    {
                        _logger.LogInformation("[TTS-DEBUG] Synthesizing FIRST chunk #{Index} using SynthesizeTextChunkAsync: '{TextChunk}'", chunkIndex, cleanedChunk);
                        audioData = await _synthesizer.SynthesizeTextChunkAsync(cleanedChunk, voice);
                    }
                    else
                    {
                        _logger.LogInformation("[TTS-DEBUG] Synthesizing SUBSEQUENT chunk #{Index} using ChunkedSynthesisAsync: '{TextChunk}'", chunkIndex, cleanedChunk);
                        if (_synthesizer is ProgressiveTTSSynthesizer progressiveSynthesizer)
                        {
                            var chunkParts = new List<byte[]>();
                            await progressiveSynthesizer.ChunkedSynthesisAsync(cleanedChunk, voice, chunkPart =>
                            {
                                chunkParts.Add(chunkPart);
                            });
                            using var ms = new MemoryStream();
                            foreach (var part in chunkParts)
                            {
                                ms.Write(part, 0, part.Length);
                            }
                            audioData = ms.ToArray();
                        }
                        else
                        {
                            _logger.LogWarning("[TTS-DEBUG] Synthesizer is not ProgressiveTTSSynthesizer. Falling back to SynthesizeTextChunkAsync for subsequent chunk #{Index}", chunkIndex);
                            audioData = await _synthesizer.SynthesizeTextChunkAsync(cleanedChunk, voice); 
                        }
                    }
                    lock (audioChunkLock)
                    {
                        audioChunks[chunkIndex] = audioData;
                    }
                    audioChunkReady.Release();
                    _logger.LogInformation("[TTS-DEBUG] TTS completed for chunk #{Index}: '{TextChunk}' ({Size} bytes)", chunkIndex, textChunk, audioData.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing TTS for chunk #{Index}: {Error}", chunkIndex, ex.Message);
                }
            }

            Task audioProcessingTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await audioChunkReady.WaitAsync();
                        bool sentAny;
                        do
                        {
                            sentAny = false;
                            byte[]? chunk = null;
                            lock (audioChunkLock)
                            {
                                if (audioChunks.TryGetValue(nextChunkIndex, out chunk))
                                {
                                    audioChunks.Remove(nextChunkIndex);
                                    nextChunkIndex++;
                                    sentAny = true;
                                }
                            }
                            if (sentAny && chunk != null)
                            {
                                await SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(chunk), index = nextChunkIndex - 1 });
                                _logger.LogInformation("[WEBSOCKET-DEBUG] Sent audio chunk #{Index} ({Size} bytes)", nextChunkIndex - 1, chunk.Length);
                            }
                        } while (sentAny);
                        lock (audioChunkLock)
                        {
                            if (isResponseComplete && audioChunks.Count == 0) break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in audio processing task");
                }
            });

            int currentChunkIndex = 0;
            List<Task> ttsTasks = new List<Task>();
            bool firstChunkSent = false;

            fullReply = await streamingChatService.GenerateStreamingResponseAsync(
                _chatLogManager.GetMessages(),
                async token =>
                {
                    try
                    {
                        await SendEventAsync(webSocket, "token", new { token });
                        sb.Append(token);
                        if (token.Length > 0 && ShouldFlush(sb, token[token.Length - 1], firstChunkSent))
                        {
                            string textChunk = FlushSegmentAtSentenceBoundary(sb, firstChunkSent);
                            if (!string.IsNullOrWhiteSpace(textChunk))
                            {
                                int chunkIndex = currentChunkIndex++;
                                var textPreview = textChunk.Length <= 30 ? textChunk : textChunk.Substring(0, 30) + "...";
                                _logger.LogInformation("[CHUNK-DEBUG] Generated chunk #{Index}: '{Text}' (FirstChunk: {IsFirst})", chunkIndex, textPreview, !firstChunkSent);
                                var task = Task.Run(() => StartTtsTaskAsync(textChunk, chunkIndex, !firstChunkSent));
                                ttsTasks.Add(task);
                                if (!firstChunkSent) firstChunkSent = true;
                            }
                            else
                            {
                                _logger.LogWarning("[CHUNK-DEBUG] No chunk generated despite ShouldFlush returning true");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing token in streaming response");
                    }
                });

            if (sb.Length > 0)
            {
                try
                {
                    string remainingText = sb.ToString().Trim();
                    sb.Clear();
                    if (!string.IsNullOrWhiteSpace(remainingText) && remainingText.Length >= 5)
                    {
                        _logger.LogInformation("[FINAL-CHUNK-DEBUG] Processing remaining text: {TextChunk} (FirstChunk: {IsFirst})", remainingText.Length <= 30 ? remainingText : remainingText.Substring(0, 30) + "...", !firstChunkSent);
                        int finalChunkIndex = currentChunkIndex++;
                        var task = Task.Run(() => StartTtsTaskAsync(remainingText, finalChunkIndex, !firstChunkSent));
                        ttsTasks.Add(task);
                    }
                    else
                    {
                        _logger.LogInformation("[FINAL-CHUNK-DEBUG] Remaining text too short, skipping: '{Text}'", remainingText);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing remaining text");
                }
            }

            _logger.LogDebug("Waiting for {Count} TTS tasks to complete", ttsTasks.Count);
            await Task.WhenAll(ttsTasks);
            _logger.LogDebug("Waiting for audio processing task to complete");
            lock (audioChunkLock)
            {
                isResponseComplete = true;
                audioChunkReady.Release();
            }
            await audioProcessingTask;
            return fullReply;
        }

        private async Task<string> HandleNonStreamingChatResponseAsync(WebSocket webSocket, string prompt)
        {
            var voice = _pipelineOptions.TtsVoice;
            string reply = await _chatService.GenerateResponseAsync(_chatLogManager.GetMessages());
            await SendEventAsync(webSocket, "token", new { token = reply });
            _logger.LogInformation("Using TTS voice: {Voice}", voice);
            var audioOut = await _synthesizer.SynthesizeAsync(reply, voice);
            await SendEventAsync(webSocket, "audio-chunk", new { chunk = Convert.ToBase64String(audioOut) });
            return reply;
        }

        private async void LogAndSendFinalEvents(WebSocket webSocket, string reply) // Changed to async void for now, will await SendEventAsync calls
        {
            var botMsg = _chatLogManager.AddMessage(
                ChatRole.Bot,
                reply,
                _pipelineOptions.ChatModel,
                _pipelineOptions.TtsVoice);
            _logger.LogInformation("Reply: '{Reply}'", reply);

            await SendEventAsync(webSocket, "audio-done", null);
            await SendEventAsync(webSocket, "done", null);
        }

        private static async Task SendEventAsync(WebSocket webSocket, string eventName, object? data) 
        {
            var payload = JsonSerializer.Serialize(new { @event = eventName, data });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}